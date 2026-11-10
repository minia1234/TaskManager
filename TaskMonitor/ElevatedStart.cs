using System.Runtime.InteropServices;

namespace TaskMonitor;

// 항상 관리자 권한으로 실행: "가장 높은 권한으로 실행" 작업을 한 번 등록해 두고(UAC 한 번), 일반 권한으로 시작되면 그 작업을 돌려 넘긴다.
// requireAdministrator 매니페스트는 실행할 때마다 UAC를 묻고, Windows 시작 시 실행(Run 키)에서는 아예 막힌다. 작업 스케줄러는 둘 다 피한다.
internal static class ElevatedStart
{
    // 작업이 띄운 인스턴스 표시. 표준 사용자라 작업이 권한을 못 올려도 다시 넘기며 돌지 않게 한다.
    internal const string FromTaskArg = "--from-task";

    // shortcut: 다른 관리자 계정으로 UAC를 승인하면 그 계정 이름으로 등록돼 이 사용자에게서 안 보인다. 그런 환경이 생기면 SID로 바꾼다.
    private static string TaskName => $"TaskMonitor Elevated {Environment.UserName}";

    public static bool IsEnabled() => RegisteredTask() is not null;

    // 이 exe를 실행하는 작업만 쓴다(exe를 옮겼으면 옛 작업은 무시).
    private static dynamic? RegisteredTask()
    {
        try
        {
            dynamic task = StartupItems.TaskService().GetFolder(@"\").GetTask(TaskName);
            foreach (dynamic action in task.Definition.Actions)
            {
                return string.Equals((string?)action.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ? task : null;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }

        return null;
    }

    // 작업으로 관리자 권한 인스턴스를 띄웠으면 true. arg는 새 인스턴스에 넘길 인수 하나.
    public static bool TryRun(string arg)
    {
        try
        {
            dynamic? task = RegisteredTask();
            if (task is null)
            {
                return false;
            }

            task.Run(arg);
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // 관리자 권한으로만 부른다.
    public static void Set(bool on)
    {
        dynamic service = StartupItems.TaskService();
        dynamic folder = service.GetFolder(@"\");
        if (!on)
        {
            try
            {
                folder.DeleteTask(TaskName, 0);
            }
            catch (FileNotFoundException)
            {
            }

            return;
        }

        dynamic task = service.NewTask(0);
        task.RegistrationInfo.Description = "TaskMonitor를 UAC 확인 없이 관리자 권한으로 실행합니다(네트워크 측정 등).";
        task.Principal.RunLevel = 1; // 가장 높은 권한
        task.Settings.MultipleInstances = 0; // 이미 실행 중이어도 새로 띄워야 떠 있는 창을 불러온다
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.Priority = 4; // 작업 기본값(7)은 낮은 우선 순위다
        dynamic action = task.Actions.Create(0);
        action.Path = Environment.ProcessPath;
        action.Arguments = FromTaskArg + " $(Arg0)";
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        folder.RegisterTaskDefinition(TaskName, task, 6, identity.Name, null, 3); // 3 = 로그온한 사용자 토큰
    }
}
