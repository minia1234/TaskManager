using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;

namespace TaskMonitor;

internal sealed record StartupRow(string Name, string Command, string Publisher, bool Enabled, RegistryHive Hive);

// 시작 프로그램: Run 키 목록과 StartupApproved 키로 사용 여부를 읽고 바꾼다.
// 작업 관리자와 같은 방식이다. HKLM을 바꾸려면 관리자 권한이 필요하다.
// 이 앱을 Windows 시작 시 실행: HKCU Run 키에 "<exe> --tray"를 쓴다(관리자 권한 없이).
internal static class AutoStart
{
    private const string ValueName = "TaskMonitor";

    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(StartupItems.RunPath);
        using var approved = Registry.CurrentUser.OpenSubKey(StartupItems.ApprovedPath);
        // Windows 설정이나 시작 프로그램 페이지에서 끈 항목은 Run 값이 있어도 실행되지 않는다.
        return run?.GetValue(ValueName) is string && StartupItems.IsEnabled(approved?.GetValue(ValueName) as byte[]);
    }

    public static void Set(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(StartupItems.RunPath);
        if (enabled)
        {
            run.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
            using var approved = Registry.CurrentUser.OpenSubKey(StartupItems.ApprovedPath, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

internal static class StartupItems
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    internal static List<StartupRow> Enumerate()
    {
        var result = new List<StartupRow>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var run = root.OpenSubKey(RunPath);
            using var approved = root.OpenSubKey(ApprovedPath);
            if (run is null)
            {
                continue;
            }

            foreach (var name in run.GetValueNames())
            {
                string command = run.GetValue(name)?.ToString() ?? "";
                bool enabled = IsEnabled(approved?.GetValue(name) as byte[]);
                result.Add(new StartupRow(name, command, PublisherOf(command), enabled, hive));
            }
        }

        return result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static void SetEnabled(StartupRow row, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(row.Hive, RegistryView.Default);
        using var approved = root.CreateSubKey(ApprovedPath, true);

        // 첫 바이트 0x02 = 사용, 0x03 = 사용 안 함. 뒤의 8바이트는 바뀐 시각(FILETIME)이다.
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
        {
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
        }

        approved.SetValue(row.Name, bytes, RegistryValueKind.Binary);
    }

    // 값이 없으면 사용으로 본다. 첫 바이트의 가장 낮은 비트가 1이면 사용 안 함이다(0x03).
    internal static bool IsEnabled(byte[]? value) => value is null || value.Length == 0 || (value[0] & 0x01) == 0;

    // 명령에서 실행 파일 경로를 뽑는다. 따옴표가 있으면 따옴표 안, 없으면 .exe 까지.
    internal static string ExtractPath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : command.Trim('"');
        }

        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? command[..(exe + 4)] : command;
    }

    // 게시자는 실행 파일 자원을 읽어야 해서 경로별로 한 번만 조회한다(3초마다 목록을 다시 읽기 때문).
    private static readonly ConcurrentDictionary<string, string> Publishers = new(StringComparer.OrdinalIgnoreCase);

    private static string PublisherOf(string command)
    {
        string path = ExtractPath(command);
        if (path.Length == 0)
        {
            return "";
        }

        return Publishers.GetOrAdd(path, p =>
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(p).CompanyName ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        });
    }
}
