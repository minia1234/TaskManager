using System.Diagnostics;
using System.Text.Json;

namespace TaskMonitor;

// 앱 기록: 프로그램(실행 파일 이름)별로 CPU 시간과 디스크 입출력 양을 시작 이후(또는 기록 지우기 이후) 누적한다.
// 값은 샘플링 주기마다 CPU 사용률·디스크 속도에 경과 시간을 곱해 더한다. 네트워크 사용량은 Windows 기록(ETW)이 필요해서 제공하지 않는다.
// %LOCALAPPDATA%\TaskMonitor\app-history.json 에 저장해서 다음 실행에도 이어서 쓴다.
internal sealed class AppHistory
{
    internal sealed class Entry
    {
        public double CpuSeconds { get; set; }
        public double IoBytes { get; set; }
    }

    internal sealed record Row(string Name, double CpuSeconds, double IoBytes, DateTime Since);

    private sealed class Saved
    {
        public DateTime Since { get; set; } = DateTime.Now;
        public Dictionary<string, Entry> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMonitor", "app-history.json");

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Saved _data = new();
    private double _lastTime = double.NaN;

    public AppHistory()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                _data = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) ?? new Saved();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 손상된 기록 파일은 빈 기록으로 시작한다.
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

            foreach (var group in processes.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!_data.Apps.TryGetValue(group.Key, out var entry))
                {
                    entry = new Entry();
                    _data.Apps[group.Key] = entry;
                }

                double cpu = group.Sum(p => p.CpuPercent) / 100.0 * logicalProcessors * elapsed;
                double io = group.Sum(p => p.DiskBytesPerSec) * elapsed;
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
                .Select(kv => new Row(kv.Key, kv.Value.CpuSeconds, kv.Value.IoBytes, _data.Since))
                .OrderByDescending(r => r.CpuSeconds)
                .ToList();
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
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch (IOException)
        {
            // 저장 실패는 화면 동작을 막지 않는다.
        }
    }
}
