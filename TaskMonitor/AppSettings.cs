using System.Text.Json;

namespace TaskMonitor;

// 사용자 설정 (%LOCALAPPDATA%\TaskMonitor\settings.json)
internal sealed class AppSettings
{
    public int UpdateIntervalMs { get; set; } = 1000;      // 0 이하 = 일시 중지
    public bool AlwaysOnTop { get; set; }
    public bool MinimizeOnUse { get; set; }
    public bool HideWhenMinimized { get; set; }
    public int ThresholdPercent { get; set; } = 70;
    public bool LogEnabled { get; set; } = true;
    public bool GroupProcesses { get; set; } = true;
    public bool ProcessTree { get; set; }                  // 프로세스 목록을 부모-자식 트리로 보기
    public string ColorTheme { get; set; } = "light";     // light | dark | system
    public string Language { get; set; } = "ko";          // ko | en (다음 실행부터 적용)
    public Dictionary<string, List<string>> Columns { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMonitor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 손상된 설정 파일은 기본값으로 시작한다.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // 저장 실패는 화면 동작을 막지 않는다.
        }
    }
}
