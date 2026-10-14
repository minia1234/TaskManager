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

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
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
