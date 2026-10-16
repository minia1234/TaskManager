using System.Text;
using System.Text.Json;

namespace TaskMonitor;

// 기록 파일 저장 공통 부분. 한 줄에 기록 한 건(JSON)을 쓰고, 크기 한도를 넘으면 ".1" 파일로 밀어낸 뒤 새 파일에 이어 쓴다.
// 기록 파일은 %LOCALAPPDATA%\TaskMonitor 아래에 둔다. 쓰기 실패는 화면 동작을 막지 않는다.
internal static class RecordFolder
{
    internal static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMonitor");
}

internal sealed class JsonLinesFile
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _gate = new();
    private readonly long _maxBytes;

    public JsonLinesFile(string filePath, long maxBytes)
    {
        FilePath = filePath;
        _maxBytes = maxBytes;
    }

    public string FilePath { get; }

    public void Append(IEnumerable<string> lines)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > _maxBytes)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllLines(FilePath, lines, Utf8);
            }
            catch (IOException)
            {
                // 파일이 잠겼거나 디스크가 막힌 경우 다음 기록에서 다시 시도한다.
            }
        }
    }

    // 오래된 파일(".1")부터 현재 파일까지 모든 줄을 순서대로 읽는다.
    public IEnumerable<string> ReadAll()
    {
        foreach (var file in new[] { FilePath + ".1", FilePath })
        {
            if (!File.Exists(file))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file, Utf8))
            {
                yield return line;
            }
        }
    }
}

// 자원 기록 한 건(5초마다): CPU·메모리·GPU 사용률, GPU 온도, VRAM 사용량.
internal sealed record ResourceSample(
    DateTime Time,
    double CpuPercent,
    double RamPercent,
    long RamUsedBytes,
    double GpuPercent,
    int? GpuTempC,
    long? VramUsedBytes);

// 장기 자원 기록: 다음 실행에도 이어져서 남는다. 리포트와 CSV 내보내기가 이 파일을 읽는다.
internal sealed class ResourceLog
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private const long MaxFileBytes = 20L * 1024 * 1024;

    private readonly JsonLinesFile _file;

    public ResourceLog(string filePath)
    {
        _file = new JsonLinesFile(filePath, MaxFileBytes);
    }

    public string FilePath => _file.FilePath;

    public void Add(ResourceSample sample)
    {
        _file.Append(new[] { JsonSerializer.Serialize(sample, RecordJson.Options) });
    }

    public IReadOnlyList<ResourceSample> ReadRange(DateTime from, DateTime to)
    {
        var result = new List<ResourceSample>();
        foreach (var line in _file.ReadAll())
        {
            try
            {
                var sample = JsonSerializer.Deserialize<ResourceSample>(line);
                if (sample is not null && sample.Time >= from && sample.Time < to)
                {
                    result.Add(sample);
                }
            }
            catch (JsonException)
            {
                // 손상된 줄은 건너뛴다.
            }
        }

        return result;
    }
}
