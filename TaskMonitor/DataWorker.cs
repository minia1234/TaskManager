using System.Diagnostics;

namespace TaskMonitor;

// 창이 안 보일 때(최소화, 트레이)는 화면용 작업을 줄인다. 기록·경고에 쓰는 측정은 그대로 둔다.
internal static class AppState
{
    public static volatile bool Background;
}

// 백그라운드 데이터 작업자: 프로세스 목록과 표의 행을 만든다. UI 스레드는 완성된 결과(Frame)만 받아서 그린다.
// 서비스, 시작 프로그램, 사용자 열거는 Windows 서비스 관리자나 네트워크 조회에 따라 오래 걸리거나 막힐 수 있어서
// 별도 작업으로 돌린다. 그 작업이 막혀도 프로세스와 성능 갱신은 계속된다.
internal sealed class DataWorker : IDisposable
{
    // Rows: 현재 페이지의 최신 행 목록(바뀌지 않았으면 같은 객체). Error: 이번 작업 중 발생한 오류 메시지.
    internal sealed record Frame(int Page, Snapshot? Snapshot, PerfSnapshot Perf, IReadOnlyList<Row>? Rows, string? Error);

    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(3);
    private const int SlowStageMs = 300;
    private const int PageCount = 8;

    private readonly Action<Frame> _publish;
    private readonly SystemPerf _perf;
    private readonly CpuSampler _sampler = new();
    private readonly ProcessInfoCache _info = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly object _rowsGate = new();
    private readonly IReadOnlyList<Row>?[] _rowsByPage = new IReadOnlyList<Row>?[PageCount];
    private readonly int[] _slowBusy = new int[PageCount];
    private readonly AppHistory _history = new();
    private DateTime _lastHistorySave = DateTime.Now;

    // 장기 기록: 프로세스 시작·종료와 경고(활동 기록), 5초마다 CPU·메모리·GPU 값(자원 기록).
    private readonly ActivityLog _activity = new(Path.Combine(RecordFolder.Path, "activity-log.jsonl"));
    private readonly ResourceLog _resources = new(Path.Combine(RecordFolder.Path, "resource-log.jsonl"));
    private readonly ProcessEventTracker _tracker = new();
    private readonly AlertRules _alerts = new();
    private readonly GameSessionTracker _games = new();
    private readonly EtwMonitor _etw = new();
    private volatile bool _alertsOnly;
    private double? _cpuTemp;
    private volatile IReadOnlyList<double> _fanRpm = Array.Empty<double>();
    private DateTime _lastResourceWrite = DateTime.MinValue;
    private int _activityShown = -1;
    private string? _lastSideError;

    private volatile bool _stop;
    private volatile int _page;
    private volatile bool _rebuildRequested;
    private volatile bool _sampleRequested;
    private volatile int _intervalMs;
    private volatile bool _grouped;
    private volatile bool _tree;
    private volatile bool _logEnabled;
    private volatile int _threshold;
    private readonly object _alertGate = new();
    private string? _alertText;
    private DateTime _alertAt;
    private volatile Snapshot? _snapshot;
    private DateTime _lastSlow = DateTime.MinValue;
    private volatile string _stage = "대기";
    private long _stageStarted = Environment.TickCount64;

    public DataWorker(Action<Frame> publish, SystemPerf perf, int page, int intervalMs, bool grouped, bool logEnabled, int threshold)
    {
        _publish = publish;
        _perf = perf;
        _page = page;
        _intervalMs = intervalMs;
        _grouped = grouped;
        _logEnabled = logEnabled;
        _threshold = threshold;
        _thread = new Thread(Run) { IsBackground = true, Name = "TaskMonitor data" };
    }

    public void Start()
    {
        _etw.Start();
        _thread.Start();
    }

    // 멈춤 감시용: 작업 스레드가 지금 어느 단계에 있고, 그 단계에 들어간 지 몇 ms인지
    public string CurrentStage => _stage;
    public long StageAgeMs => Environment.TickCount64 - Volatile.Read(ref _stageStarted);

    // 화면 전환: 마지막 샘플로 해당 페이지 행만 새로 만든다(CPU는 다시 재지 않는다).
    public void RequestPage(int page)
    {
        _page = page;
        _rebuildRequested = true;
        _wake.Set();
    }

    // 지금 바로 전체 샘플링 (새로 고침, 작업 직후)
    public void RequestNow()
    {
        _sampleRequested = true;
        _wake.Set();
    }

    // 0 이하 = 일시 중지 (화면 전환과 새로 고침으로만 갱신)
    public void SetInterval(int ms)
    {
        _intervalMs = ms;
        _wake.Set();
    }

    public void Configure(bool grouped, bool logEnabled, int threshold)
    {
        _grouped = grouped;
        _logEnabled = logEnabled;
        _threshold = threshold;
        _rebuildRequested = true;
        _wake.Set();
    }

    // 이벤트 페이지: 경고만 보여 줄지.
    public void SetAlertsOnly(bool alertsOnly)
    {
        _alertsOnly = alertsOnly;
        _rebuildRequested = true;
        _wake.Set();
    }

    // 프로세스 목록을 트리(부모 아래 자식)로 보여 줄지. 켜면 "이름별 합산"은 적용되지 않는다.
    public void SetTree(bool tree)
    {
        _tree = tree;
        _rebuildRequested = true;
        _wake.Set();
    }

    public ActivityLog Activity => _activity;

    // CPU 온도(HWiNFO, 없으면 Afterburner 공유 메모리). 둘 다 꺼져 있으면 null.
    public double? CpuTemperatureC => _cpuTemp;

    // 팬 회전수(RPM, 최대 세 개). HWiNFO가 꺼져 있으면 비어 있다.
    public IReadOnlyList<double> FanRpm => _fanRpm;

    public ResourceLog Resources => _resources;

    public IReadOnlyList<AppHistory.Row> AppRows => _history.Snapshot();

    // 최근 경고 한 줄(상태 바에 보인다). 발생 후 5분이 지나면 null이 된다.
    public string? LastAlert
    {
        get
        {
            lock (_alertGate)
            {
                return _alertText is not null && DateTime.Now - _alertAt < TimeSpan.FromMinutes(5) ? _alertText : null;
            }
        }
    }

    private void Run()
    {
        while (!_stop)
        {
            int interval = _intervalMs;
            bool signaled = _wake.WaitOne(interval > 0 ? interval : Timeout.Infinite);
            if (_stop)
            {
                break;
            }

            // 주기가 끝났거나 새로 고침을 요청받았을 때만 샘플링한다. 화면 전환 같은 요청은 행만 다시 만든다.
            bool sample = !signaled || _sampleRequested || _snapshot is null;
            Build(sample);
        }
    }

    private void Build(bool sample)
    {
        bool rebuild = _rebuildRequested;
        bool forceSample = _sampleRequested;
        _rebuildRequested = false;
        _sampleRequested = false;

        int page = _page;
        try
        {
            if (sample || forceSample)
            {
                var snap = RunStage("CPU 샘플", _sampler.Sample);
                if (snap is not null)
                {
                    _snapshot = snap;
                    _history.Update(snap.Processes, snap.LogicalProcessors);
                    if (DateTime.Now - _lastHistorySave >= TimeSpan.FromSeconds(60))
                    {
                        _lastHistorySave = DateTime.Now;
                        RunSideStage("앱 기록 저장", _history.Save);
                    }

                    _info.BeginTick(40);
                    _info.Prune(snap.Processes.Select(p => (p.Pid, p.CreateTime)));
                    if (_logEnabled && snap.TotalCpuPercent >= _threshold)
                    {
                        RunSideStage("기록 파일", () => SpikeLog.Append(snap));
                    }

                    RunSideStage("장기 기록", () => RecordActivity(snap));
                    rebuild = true;
                }
            }

            var snapshot = _snapshot;
            var perf = _perf.Latest;
            // 아무도 보지 않는 표는 만들지 않는다. 창이 다시 보이면 MainForm이 RequestPage로 다시 만들게 한다.
            if (snapshot is not null && !AppState.Background)
            {
                bool slowDue = forceSample || rebuild || DateTime.Now - _lastSlow >= SlowInterval;
                switch (page)
                {
                    case MainForm.PageProcesses when rebuild || GetRows(page) is null:
                        SetRows(page, RunStage("프로세스 행", () => _tree
                            ? RowBuilders.ProcessTree(snapshot, _info, perf.GpuByPid)
                            : RowBuilders.Processes(snapshot, _info, _grouped, perf.GpuByPid)));
                        break;
                    case MainForm.PageEvents when rebuild || _activity.Version != _activityShown || GetRows(page) is null:
                        _activityShown = _activity.Version;
                        SetRows(page, RunStage("이벤트 행", () => RowBuilders.Activity(
                            _alertsOnly ? _activity.Recent().Where(e => e.Kind == ActivityLog.Alert).ToList() : _activity.Recent())));
                        break;
                    case MainForm.PageHistory when rebuild || GetRows(page) is null:
                        SetRows(page, RunStage("앱 기록 행", () => RowBuilders.History(_history.Snapshot())));
                        break;
                    case MainForm.PageDetails when rebuild || GetRows(page) is null:
                        SetRows(page, RunStage("세부 정보 행", () => RowBuilders.Details(snapshot, _info, perf.GpuByPid)));
                        break;
                    case MainForm.PageServices or MainForm.PageStartup or MainForm.PageUsers when slowDue:
                        StartSlowIfIdle(page);
                        break;
                }
            }

            Publish(new Frame(page, snapshot, perf, GetRows(page), null));
        }
        catch (Exception ex)
        {
            Publish(new Frame(page, _snapshot, _perf.Latest, GetRows(page), ex.Message));
        }
    }

    // 종료 중에는 화면이 이미 없을 수 있다. 그때 보내면 작업 스레드에서 예외가 나 프로세스가 죽는다.
    private void Publish(Frame frame)
    {
        if (_stop)
        {
            return;
        }

        try
        {
            _publish(frame);
        }
        catch (InvalidOperationException)
        {
            // 창이 닫히는 중이다.
        }
    }

    // 파일 쓰기 같은 기록 단계는 실패해도 화면 갱신을 막지 않는다(예: spike-log.csv를 Excel이 잠금).
    // 같은 실패가 매초 반복되므로 내용이 바뀔 때만 freeze.log에 남긴다.
    private void RunSideStage(string name, Action work)
    {
        try
        {
            RunStage(name, work);
        }
        catch (Exception ex)
        {
            string message = $"기록 단계 '{name}' 실패: {ex.GetType().Name} {ex.Message}";
            if (message != _lastSideError)
            {
                _lastSideError = message;
                FreezeLog.Write(message);
            }
        }
    }

    // 샘플마다: 프로세스 시작·종료와 경고를 활동 기록에 남기고, 자원 기록 주기(5초)가 지났으면 CPU·메모리·GPU 값을 남긴다.
    private void RecordActivity(Snapshot snap)
    {
        var now = DateTime.Now;
        NetTable.Refresh();
        var perf = _perf.Latest;
        var gpus = GpuDetailMonitor.Latest;

        var events = _tracker.Update(snap.Processes, now);
        var sensors = HwinfoSensors.Read();
        var cpuTemp = HwinfoSensors.CpuTemperatureC(sensors) ?? AfterburnerSensors.CpuTemperatureC();
        _cpuTemp = cpuTemp;
        _fanRpm = sensors.Where(r => r.Kind == HwinfoSensors.KindFan && r.Unit.Contains("RPM", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Value).Take(3).ToList();
        _etw.Prune(snap.Processes.Select(p => p.Pid).ToHashSet());
        var frames = _etw.Frames(now);
        var rates = _etw.Rates(now);
        var alerts = _alerts.Evaluate(now, snap, perf, gpus, DiskHealthMonitor.Latest, cpuTemp,
            _etw.State == EtwState.Running ? rates : null);
        EtwLatest.Publish(frames, rates, _etw.Totals(), _etw.State, _etw.Status);
        var games = _games.Update(now, snap, perf, gpus, frames, Win32.ForegroundWindowState());
        var recorded = new List<ActivityEvent>(events.Count + alerts.Count + games.Count);
        recorded.AddRange(events);
        recorded.AddRange(alerts);
        recorded.AddRange(games);
        _activity.Add(recorded);
        if (alerts.Count > 0)
        {
            var last = alerts[^1];
            lock (_alertGate)
            {
                _alertText = $"{now:HH:mm:ss} {last.Subject}: {last.Detail}";
                _alertAt = now;
            }
        }

        if (now - _lastResourceWrite >= ResourceLog.Interval && perf.Memory.Total > 0)
        {
            _lastResourceWrite = now;
            long used = perf.Memory.Total - perf.Memory.Available;
            double gpuPercent = perf.Gpus.Count > 0 ? perf.Gpus.Max(g => g.Utilization) : 0;
            _resources.Add(new ResourceSample(
                now,
                snap.TotalCpuPercent,
                100.0 * used / perf.Memory.Total,
                used,
                gpuPercent,
                gpus.Select(g => g.TemperatureC).Max(),
                gpus.Count > 0 ? gpus.Sum(g => g.VramUsedBytes ?? 0) : null));
        }
    }

    // 작업 스레드의 한 단계. 이름을 남겨서 멈췄을 때 감시 스레드가 어느 단계인지 알 수 있게 하고,
    // 오래 걸린 단계는 기록한다.
    private T RunStage<T>(string name, Func<T> work)
    {
        _stage = name;
        Volatile.Write(ref _stageStarted, Environment.TickCount64);
        var clock = Stopwatch.StartNew();
        try
        {
            return work();
        }
        finally
        {
            EndStage(name, clock);
        }
    }

    private void RunStage(string name, Action work)
    {
        RunStage<bool>(name, () => { work(); return true; });
    }

    private void EndStage(string name, Stopwatch clock)
    {
        long ms = clock.ElapsedMilliseconds;
        if (ms >= SlowStageMs)
        {
            FreezeLog.Write($"작업 스레드 단계 '{name}' {ms}ms");
        }

        _stage = "대기";
    }

    // 느린 열거는 한 번에 하나만 돌린다. 이전 작업이 아직 끝나지 않았으면 새로 시작하지 않는다.
    private void StartSlowIfIdle(int page)
    {
        if (Interlocked.CompareExchange(ref _slowBusy[page], 1, 0) != 0)
        {
            return;
        }

        _lastSlow = DateTime.Now;
        Task.Run(() => RunSlow(page));
    }

    private void RunSlow(int page)
    {
        string? error = null;
        try
        {
            IReadOnlyList<Row> rows = page switch
            {
                MainForm.PageServices => RowBuilders.Services(ServiceList.Enumerate()),
                MainForm.PageStartup => RowBuilders.Startup(StartupItems.Enumerate()),
                _ => RowBuilders.Users(Sessions.Enumerate()),
            };
            SetRows(page, rows);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _slowBusy[page], 0);
        }

        Publish(new Frame(page, _snapshot, _perf.Latest, GetRows(page), error));
    }

    private IReadOnlyList<Row>? GetRows(int page)
    {
        lock (_rowsGate)
        {
            return _rowsByPage[page];
        }
    }

    private void SetRows(int page, IReadOnlyList<Row> rows)
    {
        lock (_rowsGate)
        {
            _rowsByPage[page] = rows;
        }
    }

    // 앱 기록을 지운다. 다음 샘플링 때 화면이 빈 목록으로 바뀐다.
    public void ResetHistory()
    {
        _history.Reset();
        _rebuildRequested = true;
        _wake.Set();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive)
        {
            _thread.Join(2000);
        }

        RunSideStage("앱 기록 저장", _history.Save);
        _etw.Dispose();
        // 작업 스레드가 아직 막혀 있으면(도메인 계정 조회 등) 깨어났을 때 쓸 수 있게 이벤트를 남겨 둔다.
        if (!_thread.IsAlive)
        {
            _wake.Dispose();
        }
    }
}
