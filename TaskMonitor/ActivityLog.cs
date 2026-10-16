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
                    events.Add(new ActivityEvent(now, ActivityLog.Start, p.Name, $"PID {p.Pid}, 부모 PID {p.ParentPid}", p.Pid));
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
    private const int GpuJunctionLimitC = 100;
    private const int DiskTempLimitC = 70;
    private const int Hysteresis = 5;
    private static readonly TimeSpan CpuSustain = TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, bool> _active = new();
    private DateTime? _cpuHighSince;

    public IReadOnlyList<ActivityEvent> Evaluate(
        DateTime now, Snapshot snapshot, PerfSnapshot perf, IReadOnlyList<GpuDetail> gpus, IReadOnlyDictionary<int, DiskHealthInfo> disks)
    {
        var alerts = new List<ActivityEvent>();

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
            ? $"전체 {snapshot.TotalCpuPercent:F0}%"
            : $"전체 {snapshot.TotalCpuPercent:F0}%, 상위: {top.Name}({top.Pid}) {top.CpuPercent:F0}%";
        Edge(alerts, now, "cpu", cpuSustained ? 100 : 0, 100, "CPU 높음 지속", cpuDetail);

        // 메모리: 사용 중인 비율이 90%를 넘으면 경고한다.
        var memory = perf.Memory;
        if (memory.Total > 0)
        {
            double ramPercent = 100.0 * (memory.Total - memory.Available) / memory.Total;
            Edge(alerts, now, "ram", ramPercent, RamPercentLimit, "메모리 부족", $"사용 {ramPercent:F0}%");
        }

        // GPU: 코어·핫스팟 중 높은 온도와 메모리 접합부 온도를 본다.
        foreach (var gpu in gpus)
        {
            if (gpu.TemperatureC is not null || gpu.HotSpotC is not null)
            {
                int peak = Math.Max(gpu.TemperatureC ?? 0, gpu.HotSpotC ?? 0);
                Edge(alerts, now, "gpu-temp:" + gpu.Name, peak, GpuTempLimitC, "GPU 과열", $"{gpu.Name} {peak}°C (기준 {GpuTempLimitC}°C)");
            }

            if (gpu.MemoryJunctionC is { } junction)
            {
                Edge(alerts, now, "gpu-junction:" + gpu.Name, junction, GpuJunctionLimitC, "GPU 메모리 과열", $"{gpu.Name} 접합부 {junction}°C (기준 {GpuJunctionLimitC}°C)");
            }
        }

        // 저장 장치: SMART 온도가 있는 장치만 본다.
        foreach (var disk in disks.Values)
        {
            if (disk.TemperatureC is { } temp)
            {
                Edge(alerts, now, "disk-temp:" + disk.Number, temp, DiskTempLimitC, "디스크 과열", $"{disk.Identity.Model} {temp}°C (기준 {DiskTempLimitC}°C)");
            }
        }

        return alerts;
    }

    // 값이 기준 이상이 되는 순간 경고를 만든다. 기준보다 Hysteresis 만큼 내려가야 다시 울릴 수 있다.
    private void Edge(List<ActivityEvent> alerts, DateTime now, string key, double value, double limit, string subject, string detail)
    {
        bool active = _active.GetValueOrDefault(key);
        if (!active && value >= limit)
        {
            _active[key] = true;
            alerts.Add(new ActivityEvent(now, ActivityLog.Alert, subject, detail, 0));
        }
        else if (active && value < limit - Hysteresis)
        {
            _active[key] = false;
        }
    }
}
