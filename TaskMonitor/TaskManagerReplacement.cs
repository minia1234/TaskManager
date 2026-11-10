using System.Diagnostics;
using Microsoft.Win32;

namespace TaskMonitor;

// 작업 관리자 대체: taskmgr.exe 실행을 IFEO Debugger 값으로 TaskMonitor에 넘긴다(Process Explorer와 같은 방식).
// Ctrl+Shift+Esc, 작업 표시줄 메뉴, Ctrl+Alt+Del 화면이 모두 taskmgr.exe를 실행하므로 TaskMonitor가 열린다. HKLM이라 관리자 권한이 필요하다.
// exe만 지우면 Ctrl+Shift+Esc가 아무것도 열지 못한다. 그래서 부팅·로그온 때 exe가 없으면 Debugger 값과 자기 자신을 지우는 SYSTEM 작업을 함께 둔다.
internal static class TaskManagerReplacement
{
    internal const string IfeoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe";
    internal const string RestoreTaskName = "TaskMonitor Restore Task Manager";

    // 등록된 TaskMonitor 경로. Debugger가 없거나 다른 프로그램이면 null.
    public static string? RegisteredPath()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(IfeoKey);
        string? path = (key?.GetValue("Debugger") as string)?.Trim().Trim('"');
        // 다운로드가 붙인 "TaskMonitor (1).exe" 같은 이름도 우리 것이다. Process Explorer 같은 다른 대체 프로그램은 건드리지 않는다.
        return path is not null && Path.GetFileName(path).StartsWith("TaskMonitor", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    public static bool IsCurrent() => string.Equals(RegisteredPath(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    // 관리자 권한으로만 부른다.
    public static void Set(bool on)
    {
        string exe = Environment.ProcessPath!;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        dynamic folder = StartupItems.TaskService().GetFolder(@"\");
        if (on)
        {
            // 복구 작업은 부팅 때 SYSTEM으로 exe가 있는지 본다. 네트워크·이동식 드라이브는 그때 보이지 않아 설정을 지워 버린다.
            // %는 cmd가 변수로 풀어 다른 경로를 보게 된다.
            if (new DriveInfo(Path.GetPathRoot(exe)!).DriveType != DriveType.Fixed || exe.Contains('%'))
            {
                throw new InvalidOperationException(Strings.Localize("작업 관리자 대체는 이 PC의 고정 드라이브에 있는 exe만 쓸 수 있습니다(네트워크·이동식 드라이브, % 들어간 경로 제외)."));
            }

            using var key = root.CreateSubKey(IfeoKey, writable: true);
            key.SetValue("Debugger", $"\"{exe}\"");

            dynamic task = StartupItems.TaskService().NewTask(0);
            task.RegistrationInfo.Description = "TaskMonitor.exe가 지워졌으면 Windows 작업 관리자로 되돌립니다.";
            task.Settings.Hidden = true;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.ExecutionTimeLimit = "PT1M";
            task.Triggers.Create(8); // 부팅
            task.Triggers.Create(9); // 로그온
            dynamic action = task.Actions.Create(0);
            action.Path = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            action.Arguments = RestoreArguments(exe, @"HKLM\" + IfeoKey, RestoreTaskName);
            folder.RegisterTaskDefinition(RestoreTaskName, task, 6, "SYSTEM", null, 5); // 6 = 만들거나 바꿈, 5 = 서비스 계정
        }
        else
        {
            using (var key = root.OpenSubKey(IfeoKey, writable: true))
            {
                key?.DeleteValue("Debugger", throwOnMissingValue: false);
            }

            try
            {
                folder.DeleteTask(RestoreTaskName, 0);
            }
            catch (System.IO.FileNotFoundException)
            {
                // 작업이 이미 없다.
            }
        }
    }

    // exe가 없으면 Debugger 값을 지우고 이 작업도 지운다. 자체 검증이 HKCU 키와 없는 작업 이름으로 같은 명령을 돌려 본다.
    internal static string RestoreArguments(string exe, string regKey, string taskName) =>
        $"/d /c if not exist \"{exe}\" (reg delete \"{regKey}\" /v Debugger /f & schtasks /delete /tn \"{taskName}\" /f)";

    // 앱이 끝난 뒤 exe와 업데이트가 남긴 .old를 지운다. 닫히는 데 걸리는 시간을 몰라 1초 간격으로 30번까지 다시 해 본다.
    // shortcut: 경로에 %가 있으면 cmd가 풀어 버려 지우지 못한다(파일만 남음). 그런 경로가 실제로 보이면 PowerShell로 바꾼다.
    internal static string DeleteArguments(string exe) =>
        $"/d /c for /l %i in (1,1,30) do @(ping -n 2 127.0.0.1 >nul & del /f /q \"{exe}\" \"{exe}.old\" 2>nul & if not exist \"{exe}\" exit /b)";

    internal static Process? RunHidden(string arguments) => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    });
}
