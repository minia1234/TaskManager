using System.Text.Encodings.Web;
using System.Text.Json;

namespace TaskMonitor;

// 기록 파일은 사람도 읽으므로 한글을 \u 이스케이프 없이 쓴다.
internal static class RecordJson
{
    internal static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

// 활동 기록 한 건. Kind: start(프로세스 시작), exit(프로세스 종료), alert(경고).
// 샘플링 주기 사이에 시작하고 끝난 짧은 프로세스는 잡히지 않는다(표시 주기가 기록 해상도다).
internal sealed record ActivityEvent(DateTime Time, string Kind, string Subject, string Detail, int Pid);

internal sealed class ActivityLog
{
    public const string Start = "start";
    public const string Exit = "exit";
    public const string Alert = "alert";
    public const string Game = "game";

    private const int RecentCapacity = 2000;
    private const long MaxFileBytes = 10L * 1024 * 1024;

    private readonly JsonLinesFile _file;
    private readonly object _gate = new();
    private readonly List<ActivityEvent> _recent = new();
    private int _version;

    public ActivityLog(string filePath)
    {
        _file = new JsonLinesFile(filePath, MaxFileBytes);
        foreach (var line in _file.ReadAll())
        {
            var evt = Parse(line);
            if (evt is not null)
            {
                _recent.Add(evt);
            }
        }

        if (_recent.Count > RecentCapacity)
        {
            _recent.RemoveRange(0, _recent.Count - RecentCapacity);
        }
    }

    public string FilePath => _file.FilePath;

    // 화면이 새 기록이 생겼는지 알 때 쓴다.
    public int Version => Volatile.Read(ref _version);

    public void Add(IReadOnlyList<ActivityEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _recent.AddRange(events);
            if (_recent.Count > RecentCapacity)
            {
                _recent.RemoveRange(0, _recent.Count - RecentCapacity);
            }

            _version++;
        }

        _file.Append(events.Select(e => JsonSerializer.Serialize(e, RecordJson.Options)));
    }

    // 최근 기록(최신이 뒤). 화면 목록과 프로세스 분석 창이 쓴다.
    public IReadOnlyList<ActivityEvent> Recent()
    {
        lock (_gate)
        {
            return _recent.ToList();
        }
    }

    // 기간 안의 모든 기록을 파일에서 읽는다(리포트와 CSV 내보내기용).
    public IReadOnlyList<ActivityEvent> ReadRange(DateTime from, DateTime to)
    {
        var result = new List<ActivityEvent>();
        foreach (var line in _file.ReadAll())
        {
            var evt = Parse(line);
            if (evt is not null && evt.Time >= from && evt.Time < to)
            {
                result.Add(evt);
            }
        }

        return result;
    }

    private static ActivityEvent? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ActivityEvent>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// 샘플링 때마다 프로세스 목록을 이전 목록과 비교해 시작·종료를 만든다. 첫 샘플은 기준점이라 기록하지 않는다.
// 식별은 PID와 생성 시각으로 한다(PID 재사용 때문에 PID만으로는 같은 프로세스인지 알 수 없다).
internal sealed class ProcessEventTracker
{
    private Dictionary<ProcessKey, ProcessUsage>? _previous;

    public IReadOnlyList<ActivityEvent> Update(IReadOnlyList<ProcessUsage> processes, DateTime now)
    {
        var current = new Dictionary<ProcessKey, ProcessUsage>(processes.Count);
        foreach (var p in processes)
        {
            current[p.Key] = p;
        }

        var events = new List<ActivityEvent>();
        if (_previous is not null)
        {
            foreach (var (key, p) in current)
            {
                if (!_previous.ContainsKey(key))
                {
                    events.Add(new ActivityEvent(now, ActivityLog.Start, p.Name, Strings.Fmt("PID {0}, 부모 PID {1}", p.Pid, p.ParentPid), p.Pid));
                }
            }

            foreach (var (key, p) in _previous)
            {
                if (!current.ContainsKey(key))
                {
                    events.Add(new ActivityEvent(now, ActivityLog.Exit, p.Name, $"PID {p.Pid}", p.Pid));
                }
            }
        }

        _previous = current;
        return events;
    }
}

// 경고 규칙. 값이 기준을 넘는 순간 한 번 기록하고, 기준보다 충분히 내려갈 때까지 다시 기록하지 않는다(떨림 방지).
// CPU는 90% 이상이 20초 이어질 때만 본다(짧은 튐은 무시).
internal sealed class AlertRules
{
    private const double CpuPercentLimit = 90;
    private const double RamPercentLimit = 90;
    private const int GpuTempLimitC = 85;
    private const int CpuTempLimitC = 95;
    private const int GpuJunctionLimitC = 100;
    private const int DiskTempLimitC = 70;
    private const int Hysteresis = 5;
    private static readonly TimeSpan CpuSustain = TimeSpan.FromSeconds(20);

    private const double ProcessCpuLimit = 50;
    // 한 프로그램의 네트워크(보냄+받음)와 업로드(보냄)가 이만큼 30초 이어지면 경고한다.
    private const double ProcessNetLimit = 10 * 1024 * 1024;
    private const double ProcessUploadLimit = 2 * 1024 * 1024;
    private static readonly TimeSpan NetSustain = TimeSpan.FromSeconds(30);
    // Windows가 "응답 없음"으로 본 창(5초 무응답)이 10초 더 이어지면 경고한다.
    private static readonly TimeSpan HangSustain = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, bool> _active = new();
    private readonly Sustained _hotCpu = new(CpuSustain);
    private readonly Sustained _hotNet = new(NetSustain);
    private readonly Sustained _hotUpload = new(NetSustain);
    private readonly Sustained _hung = new(HangSustain);
    private readonly LeakWatch _leaks = new();

    // 경고 제외: 이 이름의 프로그램은 프로세스별 경고(CPU, 네트워크, 업로드, 응답 없음)를 내지 않는다.
    public IReadOnlySet<string> Excluded { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private DateTime? _cpuHighSince;

    public IReadOnlyList<ActivityEvent> Evaluate(
        DateTime now, Snapshot snapshot, PerfSnapshot perf, IReadOnlyList<GpuDetail> gpus, IReadOnlyDictionary<int, DiskHealthInfo> disks,
        double? cpuTempC = null, IReadOnlyDictionary<int, NetRate>? rates = null, IReadOnlySet<int>? hung = null)
    {
        var alerts = new List<ActivityEvent>();
        var excluded = Excluded;
        var watched = snapshot.Processes.Where(p => !excluded.Contains(p.Name)).ToList();

        // CPU 온도: HWiNFO나 Afterburner 공유 메모리로 읽을 수 있을 때만 본다(없으면 경고도 없다).
        if (cpuTempC is { } cpuTemp)
        {
            Edge(alerts, now, "cpu-temp", cpuTemp, CpuTempLimitC, "CPU 과열", Strings.Fmt("CPU {0:F0}°C (기준 {1}°C)", cpuTemp, CpuTempLimitC));
        }

        // 프로세스 하나가 전체 CPU의 50% 이상을 20초 넘게 쓰면 한 번 경고한다. 그 프로세스가 내려가거나 끝나면 다시 울릴 수 있다.
        foreach (var p in _hotCpu.Fire(now, watched.Where(p => p.CpuPercent >= ProcessCpuLimit)))
        {
            alerts.Add(new ActivityEvent(now, ActivityLog.Alert, Strings.Localize("프로세스 CPU 급증"),
                Strings.Fmt("{0}({1}) {2:F0}% (전체 CPU 50% 이상 20초 지속)", p.Name, p.Pid, p.CpuPercent), p.Pid));
        }

        // 네트워크: ETW가 도는 동안만 값이 있다(권한이 없으면 경고도 없다).
        if (rates is not null)
        {
            NetRate Rate(ProcessUsage p) => rates.GetValueOrDefault(p.Pid) ?? new NetRate(0, 0);
            foreach (var p in _hotNet.Fire(now, watched.Where(p => Rate(p) is var r && r.SentBytesPerSec + r.ReceivedBytesPerSec >= ProcessNetLimit)))
            {
                var r = Rate(p);
                alerts.Add(new ActivityEvent(now, ActivityLog.Alert, Strings.Localize("프로세스 네트워크 과다"),
                    Strings.Fmt("{0}({1}) 받음 {2} 보냄 {3} (10MB/s 이상 30초 지속)", p.Name, p.Pid,
                        RowBuilders.FormatRate(r.ReceivedBytesPerSec), RowBuilders.FormatRate(r.SentBytesPerSec)), p.Pid));
            }

            foreach (var p in _hotUpload.Fire(now, watched.Where(p => Rate(p).SentBytesPerSec >= ProcessUploadLimit)))
            {
                alerts.Add(new ActivityEvent(now, ActivityLog.Alert, Strings.Localize("프로세스 업로드 지속"),
                    Strings.Fmt("{0}({1}) 보냄 {2} (2MB/s 이상 30초 지속)", p.Name, p.Pid, RowBuilders.FormatRate(Rate(p).SentBytesPerSec)), p.Pid));
            }
        }

        if (hung is not null)
        {
            foreach (var p in _hung.Fire(now, watched.Where(p => hung.Contains(p.Pid))))
            {
                alerts.Add(new ActivityEvent(now, ActivityLog.Alert, Strings.Localize("응답 없음"),
                    Strings.Fmt("{0}({1}) 15초 넘게 응답 없음", p.Name, p.Pid), p.Pid));
            }
        }

        foreach (var (p, from) in _leaks.Update(now, watched))
        {
            alerts.Add(new ActivityEvent(now, ActivityLog.Alert, Strings.Localize("메모리 누수 의심"),
                Strings.Fmt("{0}({1}) 30분 동안 줄지 않고 늘어남: {2} → {3}", p.Name, p.Pid,
                    RowBuilders.FormatMemory(from), RowBuilders.FormatMemory(p.PrivateBytes)), p.Pid));
        }

        // CPU: 기준 이상이 지속된 시간이 20초를 넘으면 경고 상태로 본다.
        if (snapshot.TotalCpuPercent >= CpuPercentLimit)
        {
            _cpuHighSince ??= now;
        }
        else
        {
            _cpuHighSince = null;
        }

        bool cpuSustained = _cpuHighSince is { } since && now - since >= CpuSustain;
        var top = snapshot.Processes.FirstOrDefault();
        string cpuDetail = top is null
            ? Strings.Fmt("전체 {0:F0}%", snapshot.TotalCpuPercent)
            : Strings.Fmt("전체 {0:F0}%, 상위: {1}({2}) {3:F0}%", snapshot.TotalCpuPercent, top.Name, top.Pid, top.CpuPercent);
        Edge(alerts, now, "cpu", cpuSustained ? 100 : 0, 100, "CPU 높음 지속", cpuDetail);

        // 메모리: 사용 중인 비율이 90%를 넘으면 경고한다.
        var memory = perf.Memory;
        if (memory.Total > 0)
        {
            double ramPercent = 100.0 * (memory.Total - memory.Available) / memory.Total;
            Edge(alerts, now, "ram", ramPercent, RamPercentLimit, "메모리 부족", Strings.Fmt("사용 {0:F0}%", ramPercent));
        }

        // GPU: 코어·핫스팟 중 높은 온도와 메모리 접합부 온도를 본다.
        foreach (var gpu in gpus)
        {
            if (gpu.TemperatureC is not null || gpu.HotSpotC is not null)
            {
                int peak = Math.Max(gpu.TemperatureC ?? 0, gpu.HotSpotC ?? 0);
                Edge(alerts, now, "gpu-temp:" + gpu.Name, peak, GpuTempLimitC, "GPU 과열", Strings.Fmt("{0} {1}°C (기준 {2}°C)", gpu.Name, peak, GpuTempLimitC));
            }

            if (gpu.MemoryJunctionC is { } junction)
            {
                Edge(alerts, now, "gpu-junction:" + gpu.Name, junction, GpuJunctionLimitC, "GPU 메모리 과열", Strings.Fmt("{0} 접합부 {1}°C (기준 {2}°C)", gpu.Name, junction, GpuJunctionLimitC));
            }
        }

        // 저장 장치: SMART 온도가 있는 장치만 본다.
        foreach (var disk in disks.Values)
        {
            if (disk.TemperatureC is { } temp)
            {
                Edge(alerts, now, "disk-temp:" + disk.Number, temp, DiskTempLimitC, "디스크 과열", Strings.Fmt("{0} {1}°C (기준 {2}°C)", disk.Identity.Model, temp, DiskTempLimitC));
            }
        }

        return alerts;
    }

    // 프로세스별로 기준을 넘은 상태가 hold 이상 이어지면 한 번 내보낸다. 기준 아래로 내려가거나 끝나면 다시 울릴 수 있다.
    private sealed class Sustained(TimeSpan hold)
    {
        private readonly Dictionary<ProcessKey, DateTime> _since = new();
        private readonly HashSet<ProcessKey> _fired = new();

        public List<ProcessUsage> Fire(DateTime now, IEnumerable<ProcessUsage> over)
        {
            var list = over.ToList();
            var keys = list.Select(p => p.Key).ToHashSet();
            foreach (var key in _since.Keys.Where(k => !keys.Contains(k)).ToList())
            {
                _since.Remove(key);
                _fired.Remove(key);
            }

            var fire = new List<ProcessUsage>();
            foreach (var p in list)
            {
                _since.TryAdd(p.Key, now);
                if (now - _since[p.Key] >= hold && _fired.Add(p.Key))
                {
                    fire.Add(p);
                }
            }

            return fire;
        }
    }

    // 메모리 누수 의심: 1분마다 개인 바이트(커밋)를 보고, 30분 동안 한 번도 줄지 않으면서 500MB 넘게 늘어난 프로세스를 한 번 알린다.
    // 1% 넘게 줄면 처음부터 다시 잰다(GC나 캐시 정리로 줄어드는 프로그램은 누수로 보지 않는다).
    internal sealed class LeakWatch
    {
        private static readonly TimeSpan Step = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
        private const long MinGrowth = 500L * 1024 * 1024;

        private readonly Dictionary<ProcessKey, (DateTime Since, long From, long Last, bool Fired)> _watch = new();
        private DateTime _lastStep;

        public List<(ProcessUsage Process, long From)> Update(DateTime now, IReadOnlyList<ProcessUsage> processes)
        {
            var fired = new List<(ProcessUsage, long)>();
            if (now - _lastStep < Step)
            {
                return fired;
            }

            _lastStep = now;
            var alive = new HashSet<ProcessKey>();
            foreach (var p in processes)
            {
                alive.Add(p.Key);
                if (!_watch.TryGetValue(p.Key, out var w) || p.PrivateBytes < w.Last - w.Last / 100)
                {
                    _watch[p.Key] = (now, p.PrivateBytes, p.PrivateBytes, false);
                    continue;
                }

                w.Last = Math.Max(w.Last, p.PrivateBytes);
                if (!w.Fired && now - w.Since >= Window && w.Last - w.From >= MinGrowth)
                {
                    w.Fired = true;
                    fired.Add((p, w.From));
                }

                _watch[p.Key] = w;
            }

            foreach (var key in _watch.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _watch.Remove(key);
            }

            return fired;
        }
    }

    // 값이 기준 이상이 되는 순간 경고를 만든다. 기준보다 Hysteresis 만큼 내려가야 다시 울릴 수 있다.
    private void Edge(List<ActivityEvent> alerts, DateTime now, string key, double value, double limit, string subject, string detail)
    {
        bool active = _active.GetValueOrDefault(key);
        if (!active && value >= limit)
        {
            _active[key] = true;
            alerts.Add(new ActivityEvent(now, ActivityLog.Alert, Strings.Localize(subject), detail, 0));
        }
        else if (active && value < limit - Hysteresis)
        {
            _active[key] = false;
        }
    }
}
