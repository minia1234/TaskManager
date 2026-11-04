using System.Text;
using System.Windows.Forms;

namespace TaskMonitor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--check")
        {
            RunCheck(args[1]);
            return 0;
        }

        if (args.Length == 2 && args[0] == "--selftest")
        {
            return SelfTest.Run(args[1]);
        }

        // 한 번에 하나만 실행한다. 두 번째 실행은 이미 떠 있는 창을 앞으로 불러오고 끝난다(기록 파일을 두 프로세스가 덮어쓰지 않게).
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TaskMonitor.Activate", out bool first);
        if (!first)
        {
            // 사용자가 방금 실행한 이 프로세스가 앞 창 권한을 가지고 있으니, 이미 떠 있는 창이 앞으로 올 수 있게 넘겨 준다.
            AllowSetForegroundWindow(-1);
            activate.Set();
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception, fatal: true);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(activate, startInTray: args.Contains("--tray")));
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static void ReportCrash(Exception? ex, bool fatal)
    {
        FreezeLog.Crash(ex);
        string next = fatal ? Strings.Localize("프로그램을 종료합니다.") : Strings.Localize("프로그램은 계속 실행됩니다.");
        MessageBox.Show(
            Strings.Localize("예기치 않은 오류가 발생했습니다. 내용은 다음 파일에 기록했습니다.") + Environment.NewLine
            + FreezeLog.CrashPath + Environment.NewLine + next,
            "TaskMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // 화면 없이 샘플 4회를 찍어 파일에 쓴다. 측정값을 Get-Counter 등과 비교할 때 쓴다.
    private static void RunCheck(string outPath)
    {
        var sampler = new CpuSampler();
        var sb = new StringBuilder();

        sampler.Sample();
        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(1000);
            var s = sampler.Sample();
            if (s is null)
            {
                continue;
            }

            sb.AppendLine($"total={s.TotalCpuPercent:F1} dpc_int={s.DpcInterruptPercent:F1} unattributed={s.UnattributedPercent:F1} cores={s.LogicalProcessors}");
            foreach (var p in s.Processes.Take(10))
            {
                sb.AppendLine($"  {p.CpuPercent,6:F1}%  {p.Name} (pid {p.Pid})  ws={p.WorkingSetBytes / 1048576}MB threads={p.Threads} handles={p.Handles}");
            }

            foreach (var g in s.Groups.Take(5))
            {
                sb.AppendLine($"  [group] {g.CpuPercent,6:F1}%  {g.Name} x{g.Count}  ws={g.WorkingSetBytes / 1048576}MB");
            }

            sb.AppendLine();
        }

        File.WriteAllText(outPath, sb.ToString());
    }
}
