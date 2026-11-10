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
    // ko | en (다음 실행부터 적용). 처음 실행은 Windows 표시 언어를 따른다.
    public string Language { get; set; } =
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";
    public bool CloseToTray { get; set; }                  // X 단추로 끄지 않고 트레이에 숨기기
    public List<string> AlertExclusions { get; set; } = new(); // 프로세스별 경고를 내지 않을 프로그램 이름
    public bool MiniWindow { get; set; }                   // 항상 위 미니 창을 띄워 둔다
    public int? MiniX { get; set; }
    public int? MiniY { get; set; }
    public Dictionary<string, List<string>> Columns { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMonitor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                // 손으로 고친 파일에 "Columns": null이 있어도 시작이 죽지 않게 한다.
                loaded.Columns ??= new();
                loaded.AlertExclusions ??= new();
                return loaded;
            }
        }
        catch (JsonException)
        {
            // 손상된 설정 파일은 .bad로 옮기고 기본값으로 시작한다.
            SafeFile.SetAside(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 잠겨서 못 읽으면 기본값으로 시작하고, 이번 실행은 저장하지 않는다(사용자 설정을 기본값으로 덮어쓰지 않게).
            return new AppSettings { _saveDisabled = true };
        }

        return new AppSettings();
    }

    private bool _saveDisabled;

    public void Save()
    {
        if (_saveDisabled)
        {
            return;
        }

        try
        {
            SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 저장 실패는 화면 동작을 막지 않는다.
        }
    }
}
