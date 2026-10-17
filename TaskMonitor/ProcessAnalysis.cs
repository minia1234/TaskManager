using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TaskMonitor;

// 프로세스 분석 창의 내용. 한 프로세스의 실행 정보, 자원 사용, 네트워크 연결, 최근 활동을 텍스트로 모은다.
internal static class ProcessAnalysisText
{
    public static string Build(int pid, Snapshot? snapshot, PerfSnapshot? perf, ActivityLog? activity)
    {
        var usage = snapshot?.Processes.FirstOrDefault(p => p.Pid == pid);
        if (usage is null)
        {
            return $"PID {pid}: 프로세스가 없습니다. 이미 끝났을 수 있습니다.";
        }

        var info = new ProcessInfoCache();
        info.BeginTick(1);
        var detail = info.Get(pid, usage.CreateTime);
        var started = DateTime.FromFileTime(usage.CreateTime);
        var sb = new StringBuilder();

        sb.AppendLine($"이름        {usage.Name}");
        sb.AppendLine($"PID         {usage.Pid}   부모 PID {usage.ParentPid}");
        sb.AppendLine($"경로        {detail.Path}");
        sb.AppendLine($"명령줄      {detail.CommandLine}");
        sb.AppendLine($"사용자      {detail.User}   세션 {usage.SessionId}   {detail.Architecture}");
        sb.AppendLine($"시작 시각   {started:yyyy-MM-dd HH:mm:ss}   실행 {FormatSpan(DateTime.Now - started)}");
        sb.AppendLine();

        sb.AppendLine("[자원]");
        sb.AppendLine($"CPU         {usage.CpuPercent:F1}%");
        sb.AppendLine($"작업 세트   {FormatBytes(usage.WorkingSetBytes)}   개인 {FormatBytes(usage.PrivateBytes)}");
        sb.AppendLine($"스레드 {usage.Threads}   핸들 {usage.Handles}   I/O {FormatBytes((long)usage.DiskBytesPerSec)}/s");
        double gpu = perf?.GpuByPid.GetValueOrDefault(pid) ?? 0;
        long vram = perf is null ? 0 : perf.GpuVramByLuid.Values.SelectMany(v => v).Where(m => m.Pid == pid).Sum(m => m.Bytes);
        sb.AppendLine($"GPU         {gpu:F1}%   전용 VRAM {FormatBytes(vram)}");
        sb.AppendLine(EtwLatest.Frames.TryGetValue(pid, out var frame)
            ? $"FPS         {frame.Fps:F0}   프레임 간격 {frame.FrameMsAvg:F1} ms   1% 하위 {frame.OnePercentLowFps:F0} FPS"
            : $"FPS         측정 없음 (이 프로세스의 프레젠트가 최근 2초 없음, ETW 상태: {EtwLatest.Status})");
        sb.AppendLine();

        sb.AppendLine("[네트워크] (ETW: TCP·UDP 보낸·받은 바이트, 관리자 권한 없이 읽음)");
        if (EtwLatest.Totals.TryGetValue(pid, out var totals))
        {
            sb.AppendLine($"누적        보냄 {FormatBytes(totals.SentBytes)}   받음 {FormatBytes(totals.ReceivedBytes)}");
        }
        else
        {
            sb.AppendLine($"누적        측정 없음 (ETW 상태: {EtwLatest.Status})");
        }

        if (EtwLatest.Rates.TryGetValue(pid, out var rate))
        {
            sb.AppendLine($"현재 속도   보냄 {DiskHealth.FormatBytes((long)rate.SentBytesPerSec)}/s   받음 {DiskHealth.FormatBytes((long)rate.ReceivedBytesPerSec)}/s");
        }

        sb.AppendLine();
        sb.AppendLine("[네트워크 연결]");
        var connections = NetTable.Latest.TryGetValue(pid, out var list) ? list : Array.Empty<NetConnection>();
        if (connections.Count == 0)
        {
            sb.AppendLine("연결 없음");
        }

        foreach (var c in connections.Take(40))
        {
            string remote = c.Remote.Length > 0 ? c.Remote : "*";
            sb.AppendLine($"{c.Protocol,-4} {c.Local,-28} -> {remote,-28} {c.State}");
        }

        if (connections.Count > 40)
        {
            sb.AppendLine($"... 외 {connections.Count - 40}개");
        }

        sb.AppendLine();
        sb.AppendLine("[최근 활동]");
        var events = activity?.Recent().Where(e => e.Pid == pid).Reverse().Take(15).ToList() ?? new List<ActivityEvent>();
        if (events.Count == 0)
        {
            sb.AppendLine("기록된 활동 없음(이 프로그램을 연 뒤의 시작·종료만 기록된다)");
        }

        foreach (var e in events)
        {
            sb.AppendLine($"{e.Time:HH:mm:ss}  {e.Kind,-6} {e.Subject}  {e.Detail}");
        }

        return sb.ToString();
    }

    private static string FormatBytes(long bytes) => DiskHealth.FormatBytes(bytes);

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}시간 {span.Minutes}분" : $"{span.Minutes}분 {span.Seconds}초";
}

// 프로세스 분석 창: 1초마다 내용을 새로 만든다. 내용 생성은 호출한 쪽이 맡는다(최신 스냅샷을 읽도록).
internal sealed class ProcessAnalysisForm : Form
{
    private readonly Func<string> _text;
    private readonly TextBox _box = new()
    {
        Multiline = true,
        ReadOnly = true,
        Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 10F),
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public ProcessAnalysisForm(string title, Func<string> text)
    {
        Text = title;
        Width = 760;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        _text = text;
        _box.BackColor = Theme.Input;
        _box.ForeColor = Theme.Fg;
        Controls.Add(_box);

        UpdateText();
        _timer.Tick += (_, _) => UpdateText();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void UpdateText()
    {
        string next = _text();
        if (_box.Text != next)
        {
            _box.Text = next;
        }
    }
}
