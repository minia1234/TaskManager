using System.Diagnostics;
using System.Text.Json;

namespace TaskMonitor;

// 앱 기록: 프로그램(실행 파일 이름)별로 CPU 시간과 I/O 전송량을 시작 이후(또는 기록 지우기 이후) 누적한다.
// I/O 전송량은 프로세스가 주고받은 모든 I/O 바이트(파일, 네트워크, 파이프 포함)라서 디스크 전용 값이 아니다.
// 값은 샘플링 주기마다 CPU 사용률·I/O 속도에 경과 시간을 곱해 더한다. 네트워크 사용량만 따로 떼어 내려면 Windows 기록(ETW)이 필요해서 제공하지 않는다.
// %LOCALAPPDATA%\TaskMonitor\app-history.json 에 저장해서 다음 실행에도 이어서 쓴다.
internal sealed class AppHistory
{
    internal sealed class Entry
    {
        public double CpuSeconds { get; set; }
        public double IoBytes { get; set; }
    }

    // Path: 실행 파일 경로. 경로를 못 얻은 옛 기록(이름만 키로 쓴 것)은 빈 문자열이다.
    internal sealed record Row(string Name, string Path, double CpuSeconds, double IoBytes, DateTime Since);

    private sealed class Saved
    {
        public DateTime Since { get; set; } = DateTime.Now;
        public Dictionary<string, Entry> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMonitor", "app-history.json");

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<ProcessKey, string> _paths = new();
    private readonly bool _saveDisabled;
    private Saved _data = new();
    private double _lastTime = double.NaN;

    public AppHistory()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                _data = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) ?? new Saved();
                // 역직렬화한 사전은 대소문자를 구분한다. 예전 버전이 경로 대소문자만 다르게 따로 쌓은 줄은 합친다.
                var apps = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (var (path, entry) in _data.Apps ?? new())
                {
                    if (entry is null)
                    {
                        continue;
                    }

                    if (apps.TryGetValue(path, out var same))
                    {
                        same.CpuSeconds += entry.CpuSeconds;
                        same.IoBytes += entry.IoBytes;
                    }
                    else
                    {
                        apps[path] = entry;
                    }
                }

                _data.Apps = apps;
            }
        }
        catch (JsonException)
        {
            // 손상된 기록 파일은 .bad로 옮겨 두고 빈 기록으로 시작한다(다음 저장이 원본을 덮어쓰지 않게).
            SafeFile.SetAside(FilePath);
            _data = new Saved();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 읽지 못한 기록 위에 빈 기록을 저장하면 지난 기록이 사라진다. 이번 실행은 저장하지 않는다.
            _saveDisabled = true;
            _data = new Saved();
        }
    }

    // 샘플링 주기마다 호출한다. 첫 호출은 기준 시각만 잡고 값은 더하지 않는다.
    public void Update(IReadOnlyList<ProcessUsage> processes, int logicalProcessors)
    {
        lock (_gate)
        {
            double now = _clock.Elapsed.TotalSeconds;
            double elapsed = double.IsNaN(_lastTime) ? 0 : now - _lastTime;
            _lastTime = now;
            if (elapsed <= 0 || elapsed > 30)
            {
                // 첫 호출이거나 잠깐 멈췄다 돌아온 경우에는 값을 더하지 않는다(누적이 튀지 않게).
                return;
            }

            var keyed = processes.Select(p => (Key: KeyOf(p), Process: p)).ToList();
            PruneKeys(processes);
            foreach (var group in keyed.GroupBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!_data.Apps.TryGetValue(group.Key, out var entry))
                {
                    entry = new Entry();
                    _data.Apps[group.Key] = entry;
                }

                double cpu = group.Sum(k => k.Process.CpuPercent) / 100.0 * logicalProcessors * elapsed;
                double io = group.Sum(k => k.Process.DiskBytesPerSec) * elapsed;
                entry.CpuSeconds += cpu;
                entry.IoBytes += io;
            }
        }
    }

    public IReadOnlyList<Row> Snapshot()
    {
        lock (_gate)
        {
            return _data.Apps
                .Where(kv => kv.Value.CpuSeconds > 0 || kv.Value.IoBytes > 0)
                .Select(kv =>
                {
                    bool hasPath = kv.Key.Contains('\\');
                    string name = hasPath ? System.IO.Path.GetFileName(kv.Key) : kv.Key;
                    return new Row(name, hasPath ? kv.Key : "", kv.Value.CpuSeconds, kv.Value.IoBytes, _data.Since);
                })
                .OrderByDescending(r => r.CpuSeconds)
                .ToList();
        }
    }

    // 키: 실행 파일 경로. 경로를 못 얻으면 이름을 쓴다. 같은 이름이라도 경로가 다르면 다른 프로그램이다.
    // 경로는 프로세스(PID와 생성 시각)마다 한 번만 조회하고 캐시한다.
    private string KeyOf(ProcessUsage p)
    {
        if (!_paths.TryGetValue(p.Key, out var path))
        {
            path = ProcessInfoCache.PathOf(p.Pid);
            _paths[p.Key] = path;
        }

        return path.Length > 0 ? path : p.Name;
    }

    private void PruneKeys(IReadOnlyList<ProcessUsage> processes)
    {
        var alive = processes.Select(p => p.Key).ToHashSet();
        foreach (var key in _paths.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _paths.Remove(key);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _data = new Saved();
        }
    }

    public void Save()
    {
        if (_saveDisabled)
        {
            return;
        }

        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            SafeFile.WriteAllText(FilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 저장 실패는 화면 동작을 막지 않는다.
        }
    }
}
