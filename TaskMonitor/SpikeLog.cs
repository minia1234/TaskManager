using System.Text;

namespace TaskMonitor;

// 전체 CPU가 임계치 이상일 때 상위 5개 프로세스를 CSV에 남긴다.
// 간헐적으로 뛰는 CPU는 화면을 보고 있을 때만 잡히기 때문에 기록을 남긴다.
internal static class SpikeLog
{
    internal static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskMonitor",
        "spike-log.csv");

    internal static void Append(Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        bool isNew = !File.Exists(FilePath);

        var line = new StringBuilder();
        // 소수점은 항상 "."로 쓴다. 독일어·프랑스어 Windows에서 "12,5"가 되면 쉼표 구분 열이 밀린다.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv))
            .Append(',').Append(snapshot.TotalCpuPercent.ToString("F1", inv))
            .Append(',').Append(snapshot.DpcInterruptPercent.ToString("F1", inv))
            .Append(',').Append(snapshot.UnattributedPercent.ToString("F1", inv));

        foreach (var p in snapshot.Processes.Take(5))
        {
            line.Append(',').Append(FormattableString.Invariant($"{p.Name.Replace(',', '_')}({p.Pid}) {p.CpuPercent:F1}%"));
        }

        using var writer = new StreamWriter(FilePath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (isNew)
        {
            writer.WriteLine("time,total,dpc_interrupt,unattributed,top1,top2,top3,top4,top5");
        }

        writer.WriteLine(line.ToString());
    }
}
