using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;

namespace TaskMonitor;

// TaskPath가 있으면 작업 스케줄러 항목(로그온·부팅 때 실행되는 작업)이다.
internal sealed record StartupRow(string Name, string Command, string Publisher, bool Enabled, RegistryHive Hive, string? TaskPath = null)
{
    public string Source => TaskPath is not null ? "작업 스케줄러" : Hive == RegistryHive.CurrentUser ? "레지스트리(사용자)" : "레지스트리(모든 사용자)";
}

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

        result.AddRange(ScheduledTasks());
        return result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // 작업 스케줄러에서 로그온(9)·부팅(8) 때 실행되는 작업. Windows 자체 작업(\Microsoft 폴더)은 뺀다.
    // COM으로 폴더를 다 훑어야 해서 30초 동안 결과를 재사용한다(이 페이지는 3초마다 다시 읽는다).
    private static (DateTime At, List<StartupRow> Rows) _tasks = (DateTime.MinValue, new());

    private static List<StartupRow> ScheduledTasks()
    {
        if (DateTime.UtcNow - _tasks.At < TimeSpan.FromSeconds(30))
        {
            return _tasks.Rows;
        }

        var rows = new List<StartupRow>();
        try
        {
            void Walk(dynamic folder)
            {
                if (((string)folder.Path).StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                foreach (dynamic task in folder.GetTasks(1)) // 1 = 숨은 작업 포함
                {
                    try
                    {
                        AddTask(task);
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                    {
                        // 손상되었거나 읽을 수 없는 작업 하나 때문에 나머지를 놓치지 않는다.
                    }
                }

                foreach (dynamic sub in folder.GetFolders(0))
                {
                    Walk(sub);
                }
            }

            void AddTask(dynamic task)
            {
                {
                    dynamic definition = task.Definition;
                    bool atStart = false;
                    foreach (dynamic trigger in definition.Triggers)
                    {
                        atStart |= (int)trigger.Type is 8 or 9;
                    }

                    string command = "";
                    foreach (dynamic action in definition.Actions)
                    {
                        if ((int)action.Type == 0) // 0 = 프로그램 실행
                        {
                            command = $"\"{Environment.ExpandEnvironmentVariables((string?)action.Path ?? "")}\" {action.Arguments}".TrimEnd();
                            break;
                        }
                    }

                    if (atStart)
                    {
                        rows.Add(new StartupRow((string)task.Name, command, PublisherOf(command), (bool)task.Enabled, RegistryHive.CurrentUser, (string)task.Path));
                    }
                }
            }

            Walk(TaskService().GetFolder(@"\"));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // 작업 스케줄러 서비스가 꺼져 있거나 읽을 수 없으면 레지스트리 항목만 보인다.
        }

        _tasks = (DateTime.UtcNow, rows);
        return rows;
    }

    private static dynamic TaskService()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        return service;
    }

    internal static void SetEnabled(StartupRow row, bool enabled)
    {
        if (row.TaskPath is not null)
        {
            // 다른 계정이나 관리자가 만든 작업은 관리자 권한이 있어야 바뀐다(실패하면 오류 메시지로 알린다).
            TaskService().GetFolder(@"\").GetTask(row.TaskPath).Enabled = enabled;
            _tasks = (DateTime.MinValue, _tasks.Rows);
            return;
        }

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
