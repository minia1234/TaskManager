using System.Globalization;
using System.Text;

namespace TaskMonitor;

// 원인 분석 리포트와 기록 내보내기.
// 최근 1시간을 그 전 1시간과 비교해 자원 변화를 보고, 같은 기간의 경고와 프로세스 활동을 정리한다.
// 게임 프레임 속도, 프로세스별 네트워크 바이트, CPU 온도는 이 앱이 측정하지 못하므로 리포트에도 한계로 적는다.
internal static class CauseReport
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public static string Build(DateTime now, ResourceLog resources, ActivityLog activity, IReadOnlyList<AppHistory.Row> apps)
    {
        var from = now - Window;
        var previousFrom = now - Window - Window;
        var current = resources.ReadRange(from, now);
        var previous = resources.ReadRange(previousFrom, from);
        var events = activity.ReadRange(from, now);

        var sb = new StringBuilder();
        sb.AppendLine(Strings.Localize("# 원인 분석 리포트"));
        sb.AppendLine();
        sb.AppendLine(Strings.Fmt("- 분석 기간: {0:yyyy-MM-dd HH:mm} ~ {1:HH:mm} (최근 1시간)", from, now));
        sb.AppendLine(Strings.Fmt("- 비교 기간: {0:yyyy-MM-dd HH:mm} ~ {1:HH:mm} (그 전 1시간)", previousFrom, from));
        sb.AppendLine(Strings.Fmt("- 자원 기록: 이번 {0}개, 이전 {1}개 (5초 간격)", current.Count, previous.Count));
        sb.AppendLine(Strings.Fmt("- 활동 기록: {0}건", events.Count));
        sb.AppendLine();

        sb.AppendLine(Strings.Localize("## 자원 요약"));
        sb.AppendLine();
        sb.AppendLine(Strings.Localize("| 항목 | 이번 평균 | 이번 최고 | 이전 평균 | 평균 변화 |"));
        sb.AppendLine("|---|---:|---:|---:|---:|");
        AppendRow(sb, "CPU", current.Select(s => s.CpuPercent), previous.Select(s => s.CpuPercent), "%");
        AppendRow(sb, Strings.Localize("메모리"), current.Select(s => s.RamPercent), previous.Select(s => s.RamPercent), "%");
        AppendRow(sb, Strings.Localize("GPU 사용률"), current.Select(s => s.GpuPercent), previous.Select(s => s.GpuPercent), "%");
        AppendRow(sb, Strings.Localize("GPU 온도"), current.Where(s => s.GpuTempC is not null).Select(s => (double)s.GpuTempC!.Value),
            previous.Where(s => s.GpuTempC is not null).Select(s => (double)s.GpuTempC!.Value), "°C");
        sb.AppendLine();

        sb.AppendLine(Strings.Localize("## 경고"));
        sb.AppendLine();
        var alerts = events.Where(e => e.Kind == ActivityLog.Alert).ToList();
        if (alerts.Count == 0)
        {
            sb.AppendLine(Strings.Localize("이 기간에 기록된 경고는 없습니다."));
        }

        foreach (var a in alerts)
        {
            sb.AppendLine($"- {a.Time:HH:mm:ss} **{a.Subject}** — {a.Detail}");
        }

        sb.AppendLine();
        sb.AppendLine(Strings.Localize("## 프로세스 활동"));
        sb.AppendLine();
        var started = events.Where(e => e.Kind == ActivityLog.Start).GroupBy(e => e.Subject, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(10).ToList();
        if (started.Count == 0)
        {
            sb.AppendLine(Strings.Localize("이 기간에 시작된 프로세스 기록이 없습니다."));
        }
        else
        {
            sb.AppendLine(Strings.Localize("자주 시작된 프로그램(상위 10):"));
            foreach (var g in started)
            {
                sb.AppendLine(Strings.Fmt("- {0}: {1}회", g.Key, g.Count()));
            }
        }

        sb.AppendLine();
        sb.AppendLine(Strings.Localize("## 게임 세션"));
        sb.AppendLine();
        var sessions = events.Where(GameSessionTracker.IsEnded).ToList();
        if (sessions.Count == 0)
        {
            sb.AppendLine(Strings.Localize("이 기간에 끝난 게임 세션이 없습니다. 세션은 전체 화면 창이 GPU 3D 사용 30% 이상을 10초 이상 쓴 경우로 본다."));
        }

        foreach (var s in sessions)
        {
            sb.AppendLine($"- {s.Time:HH:mm} **{s.Subject}** — {s.Detail}");
        }

        sb.AppendLine();
        sb.AppendLine(Strings.Localize("## 앱별 누적 사용량 (앱 기록, 측정 시작 이후 전체)"));
        sb.AppendLine();
        if (apps.Count == 0)
        {
            sb.AppendLine(Strings.Localize("앱 기록이 없습니다."));
        }
        else
        {
            sb.AppendLine(Strings.Localize("| 프로그램 | CPU 시간(초) | I/O 전송량 |"));
            sb.AppendLine("|---|---:|---:|");
            foreach (var app in apps.Take(10))
            {
                sb.AppendLine($"| {app.Name} | {app.CpuSeconds:F0} | {DiskHealth.FormatBytes((long)app.IoBytes)} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine(Strings.Localize("## 해석"));
        sb.AppendLine();
        foreach (var line in Hints(current, previous, alerts))
        {
            sb.AppendLine($"- {line}");
        }

        sb.AppendLine();
        sb.AppendLine(Strings.Localize("## 측정하지 못하는 것"));
        sb.AppendLine();
        sb.AppendLine(Strings.Localize("- 프레임 속도: 프레젠트(DXGI)를 보내는 프로그램만 잰다. 프레젠트를 쓰지 않는 프로그램은 FPS가 없다."));
        sb.AppendLine(Strings.Localize("- 네트워크 바이트: TCP·UDP 보내기와 받기를 프로세스별로 잰다(ETW, 관리자 권한 없음). 연결별 세부 통계는 제공하지 않는다."));
        sb.AppendLine(Strings.Localize("- CPU 온도와 팬 속도: HWiNFO가 실행 중이고 공유 메모리 지원이 켜져 있을 때만 읽는다."));
        sb.AppendLine(Strings.Localize("- 샘플 주기(기본 1초) 사이에 시작하고 끝난 짧은 프로세스는 기록되지 않는다."));
        return sb.ToString();
    }

    // 자원 값 평균·최고와 이전 평균 대비 변화를 한 줄로 쓴다. 기록이 없으면 "-".
    private static void AppendRow(StringBuilder sb, string name, IEnumerable<double> current, IEnumerable<double> previous, string unit)
    {
        var now = current.ToList();
        var before = previous.ToList();
        if (now.Count == 0)
        {
            sb.AppendLine($"| {name} | - | - | - | - |");
            return;
        }

        double avg = now.Average();
        string change = before.Count == 0 ? "-" : $"{avg - before.Average():+0.0;-0.0;0.0} {unit}";
        string previousAvg = before.Count == 0 ? "-" : $"{before.Average():F1}{unit}";
        sb.AppendLine($"| {name} | {avg:F1}{unit} | {now.Max():F1}{unit} | {previousAvg} | {change} |");
    }

    private static List<string> Hints(IReadOnlyList<ResourceSample> current, IReadOnlyList<ResourceSample> previous, IReadOnlyList<ActivityEvent> alerts)
    {
        var hints = new List<string>();
        if (current.Count == 0)
        {
            hints.Add(Strings.Localize("이 기간에 자원 기록이 없습니다. 앱이 실행 중이었는지 확인하세요."));
            return hints;
        }

        double cpu = current.Average(s => s.CpuPercent);
        if (cpu >= 50)
        {
            hints.Add(Strings.Fmt("CPU 평균이 {0:F0}%로 높습니다. 프로세스 탭에서 CPU 열을 정렬해 상위 프로그램을 확인하세요.", cpu));
        }

        if (previous.Count > 0 && cpu - previous.Average(s => s.CpuPercent) >= 10)
        {
            hints.Add(Strings.Localize("이전 1시간보다 CPU 평균이 10%p 이상 올랐습니다. 이 기간에 시작된 프로그램 목록을 확인하세요."));
        }

        if (current.Max(s => s.RamPercent) >= 90)
        {
            hints.Add(Strings.Localize("메모리 사용이 90%를 넘은 구간이 있습니다. 메모리를 많이 쓰는 프로그램을 확인하세요."));
        }

        var gpuTemps = current.Where(s => s.GpuTempC is not null).Select(s => s.GpuTempC!.Value).ToList();
        if (gpuTemps.Count > 0 && gpuTemps.Max() >= 85)
        {
            hints.Add(Strings.Fmt("GPU 온도가 최고 {0}°C까지 올랐습니다. 냉각과 전력 제한을 확인하세요.", gpuTemps.Max()));
        }

        if (alerts.Count == 0 && hints.Count == 0)
        {
            hints.Add(Strings.Localize("이 기간에 눈에 띄는 문제 신호는 없습니다."));
        }

        return hints;
    }

    // 기록 전체를 CSV 두 개로 내보낸다. 엑셀에서 바로 열리도록 UTF-8 BOM을 붙인다.
    public static (string ResourcePath, string ActivityPath) ExportCsv(ResourceLog resources, ActivityLog activity, string folder)
    {
        Directory.CreateDirectory(folder);
        var encoding = new UTF8Encoding(true);
        string resourcePath = Path.Combine(folder, "resource-log.csv");
        string activityPath = Path.Combine(folder, "activity-log.csv");

        var resourceLines = new List<string> { "time,cpu_percent,ram_percent,ram_used_bytes,gpu_percent,gpu_temp_c,vram_used_bytes" };
        foreach (var s in resources.ReadRange(DateTime.MinValue, DateTime.MaxValue))
        {
            resourceLines.Add(string.Join(',',
                s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                s.CpuPercent.ToString("F2", CultureInfo.InvariantCulture),
                s.RamPercent.ToString("F2", CultureInfo.InvariantCulture),
                s.RamUsedBytes,
                s.GpuPercent.ToString("F2", CultureInfo.InvariantCulture),
                s.GpuTempC?.ToString(CultureInfo.InvariantCulture) ?? "",
                s.VramUsedBytes?.ToString(CultureInfo.InvariantCulture) ?? ""));
        }

        var activityLines = new List<string> { "time,kind,subject,detail,pid" };
        foreach (var e in activity.ReadRange(DateTime.MinValue, DateTime.MaxValue))
        {
            activityLines.Add(string.Join(',',
                e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Csv(e.Kind),
                Csv(e.Subject),
                Csv(e.Detail),
                e.Pid));
        }

        File.WriteAllLines(resourcePath, resourceLines, encoding);
        File.WriteAllLines(activityPath, activityLines, encoding);
        return (resourcePath, activityPath);
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
