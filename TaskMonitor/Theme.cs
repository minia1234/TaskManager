using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskMonitor;

// 화면 색 토큰. 디자인 요청서(프로토타입)의 라이트·다크 값을 그대로 옮겼다.
// 모드: "light"(기본) | "dark" | "system"(Windows 앱 테마를 따른다).
// 색은 속성으로만 읽는다. 그래야 테마를 바꾼 뒤 다시 그릴 때 새 색이 반영된다.
internal static class Theme
{
    private static bool _dark;

    public static bool IsDark => _dark;

    public static Color Bg => Pick(0xFFFFFF, 0x191919);
    public static Color Fg => Pick(0x1B1B1B, 0xF2F2F2);
    public static Color Fg2 => Pick(0x5F5F5F, 0xB5B5B5);
    public static Color Fg3 => Pick(0x8A8A8A, 0x8C8C8C);
    public static Color TopBar => Pick(0xEDF3F8, 0x1C232A);
    public static Color Nav => Pick(0xF3F3F3, 0x202020);
    public static Color NavSel => Pick(0xE0ECF9, 0x2B3542);
    public static Color RowSel => Pick(0xCCE8FF, 0x0E3A5E);
    public static Color Grid => Pick(0xE5E5E5, 0x333333);
    public static Color Hover => Pick(0xEAEFF4, 0x2A3036);
    public static Color Border => Pick(0xDADADA, 0x3A3A3A);
    public static Color Input => Pick(0xFFFFFF, 0x2B2B2B);
    public static Color Accent => Pick(0x005FB8, 0x60CDFF);
    public static Color Red => Pick(0xB22222, 0xFF7676);
    public static Color WarnBg => Pick(0xFFF4CE, 0x433519);
    public static Color ErrBg => Pick(0xFDE7E9, 0x4A2828);
    public static Color OkBg => Pick(0xE6F2FB, 0x1D3346);
    public static Color Heat0 => Pick(0xD6F1FF, 0x15324A);
    public static Color Heat1 => Pick(0x9CDDFF, 0x1A4569);
    public static Color Heat2 => Pick(0x79CFFF, 0x1F5A88);
    public static Color Heat3 => Pick(0x58BEFF, 0x2470AA);

    // 설정 값(light/dark/system)을 적용한다.
    public static void Apply(string mode)
    {
        _dark = mode switch
        {
            "dark" => true,
            "system" => SystemUsesDark(),
            _ => false,
        };
    }

    // 글자 요소(라벨, 체크 상자, 입력 칸)의 글자색과 입력 칸 배경을 현재 테마에 맞춘다.
    public static void ApplyTextColors(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control is Label or CheckBox or TextBox or NumericUpDown)
            {
                control.ForeColor = Fg;
            }

            if (control is TextBox or NumericUpDown)
            {
                control.BackColor = Input;
            }

            ApplyTextColors(control);
        }
    }

    private static Color Pick(int light, int dark)
    {
        int v = _dark ? dark : light;
        return Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    // Windows 앱 테마: AppsUseLightTheme 값이 0이면 어두운 테마다.
    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
