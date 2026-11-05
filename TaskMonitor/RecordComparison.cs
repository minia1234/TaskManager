using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TaskMonitor;

// 기록 비교: 두 기간의 자원 평균·최고와 차이, 경고·게임 세션·프로세스 시작 수를 나란히 놓는다.
internal static class RecordComparison
{
    internal sealed record Period(DateTime From, DateTime To);

    public static string Build(ResourceLog resources, ActivityLog activity, Period a, Period b)
    {
        var ra = resources.ReadRange(a.From, a.To);
        var rb = resources.ReadRange(b.From, b.To);
        var ea = activity.ReadRange(a.From, a.To);
        var eb = activity.ReadRange(b.From, b.To);

        var sb = new StringBuilder();
        sb.AppendLine(Strings.Fmt("기간 A: {0:yyyy-MM-dd HH:mm} ~ {1:yyyy-MM-dd HH:mm} (자원 표본 {2}개)", a.From, a.To, ra.Count));
        sb.AppendLine(Strings.Fmt("기간 B: {0:yyyy-MM-dd HH:mm} ~ {1:yyyy-MM-dd HH:mm} (자원 표본 {2}개)", b.From, b.To, rb.Count));
        sb.AppendLine();
        sb.AppendLine($"{Strings.Localize("항목"),-12}{Strings.Localize("A 평균"),10}{Strings.Localize("B 평균"),10}{Strings.Localize("차이(B-A)"),12}{Strings.Localize("A 최고"),10}{Strings.Localize("B 최고"),10}");
        Row(sb, "CPU %", ra.Select(s => s.CpuPercent), rb.Select(s => s.CpuPercent));
        Row(sb, Strings.Localize("메모리 %"), ra.Select(s => s.RamPercent), rb.Select(s => s.RamPercent));
        Row(sb, Strings.Localize("GPU 사용 %"), ra.Select(s => s.GpuPercent), rb.Select(s => s.GpuPercent));
        Row(sb, Strings.Localize("GPU 온도 °C"), ra.Where(s => s.GpuTempC is not null).Select(s => (double)s.GpuTempC!.Value),
            rb.Where(s => s.GpuTempC is not null).Select(s => (double)s.GpuTempC!.Value));
        Row(sb, "VRAM GB", ra.Where(s => s.VramUsedBytes is not null).Select(s => s.VramUsedBytes!.Value / 1073741824.0),
            rb.Where(s => s.VramUsedBytes is not null).Select(s => s.VramUsedBytes!.Value / 1073741824.0));
        sb.AppendLine();
        sb.AppendLine(Strings.Fmt("경고: A {0}건, B {1}건", Count(ea, ActivityLog.Alert), Count(eb, ActivityLog.Alert)));
        sb.AppendLine(Strings.Fmt("게임 세션(끝난 것): A {0}회, B {1}회", Ended(ea), Ended(eb)));
        sb.AppendLine(Strings.Fmt("프로세스 시작: A {0}건, B {1}건", Count(ea, ActivityLog.Start), Count(eb, ActivityLog.Start)));
        sb.AppendLine();
        sb.AppendLine(Strings.Localize("차이는 B에서 A를 뺀 값이다. 양수면 B가 높다. 프레임 속도는 측정하지 않는다."));
        return sb.ToString();
    }

    // 각 기간은 따로 보인다(한쪽에만 표본이 있어도 다른 쪽 값은 나온다). 차이는 양쪽에 표본이 있을 때만 쓴다.
    private static void Row(StringBuilder sb, string name, IEnumerable<double> a, IEnumerable<double> b)
    {
        var la = a.ToList();
        var lb = b.ToList();
        string avgA = la.Count > 0 ? la.Average().ToString("F1") : "-";
        string avgB = lb.Count > 0 ? lb.Average().ToString("F1") : "-";
        string diff = la.Count > 0 && lb.Count > 0 ? (lb.Average() - la.Average()).ToString("+0.0;-0.0;0.0") : "-";
        string maxA = la.Count > 0 ? la.Max().ToString("F1") : "-";
        string maxB = lb.Count > 0 ? lb.Max().ToString("F1") : "-";
        sb.AppendLine($"{name,-12}{avgA,10}{avgB,10}{diff,12}{maxA,10}{maxB,10}");
    }

    private static int Count(IReadOnlyList<ActivityEvent> events, string kind) => events.Count(e => e.Kind == kind);

    private static int Ended(IReadOnlyList<ActivityEvent> events) =>
        events.Count(GameSessionTracker.IsEnded);
}

// 기록 비교 창: 기간 A와 B를 고르면 비교 표를 만든다. 파일 읽기는 백그라운드에서 한다.
internal sealed class RecordComparisonForm : Form
{
    private readonly ResourceLog _resources;
    private readonly ActivityLog _activity;
    private readonly DateTimePicker _aFrom = Picker(DateTime.Now.AddHours(-1));
    private readonly DateTimePicker _aTo = Picker(DateTime.Now);
    private readonly DateTimePicker _bFrom = Picker(DateTime.Now.AddHours(-2));
    private readonly DateTimePicker _bTo = Picker(DateTime.Now.AddHours(-1));
    private readonly Button _compare = new() { Text = Strings.Localize("비교"), Width = 90, Height = 30 };
    private readonly TextBox _result = new()
    {
        Multiline = true,
        ReadOnly = true,
        Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 10F),
    };

    public RecordComparisonForm(ResourceLog resources, ActivityLog activity)
    {
        _resources = resources;
        _activity = activity;
        Text = Strings.Localize("기록 비교");
        Width = 760;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 110, Padding = new Padding(10), AutoSize = false };
        top.Controls.Add(Caption(Strings.Localize("기간 A 시작")));
        top.Controls.Add(_aFrom);
        top.Controls.Add(Caption(Strings.Localize("끝")));
        top.Controls.Add(_aTo);
        top.Controls.Add(Caption(Strings.Localize("기간 B 시작")));
        top.Controls.Add(_bFrom);
        top.Controls.Add(Caption(Strings.Localize("끝")));
        top.Controls.Add(_bTo);
        top.Controls.Add(_compare);
        top.SetFlowBreak(_aTo, true);
        top.SetFlowBreak(_bTo, true);

        Controls.Add(_result);
        Controls.Add(top);
        _compare.Click += async (_, _) => await CompareAsync();
        // Enter로 비교를 실행한다(키보드만으로 쓸 수 있게).
        AcceptButton = _compare;
        foreach (var picker in new[] { _aFrom, _aTo, _bFrom, _bTo })
        {
            picker.BackColor = Theme.Input;
            picker.ForeColor = Theme.Fg;
        }

        // 단추는 시스템 색을 쓰면 어두운 테마에서 글자가 안 보이므로 테마 색을 직접 준다.
        _compare.FlatStyle = FlatStyle.Flat;
        _compare.BackColor = Theme.Input;
        _compare.ForeColor = Theme.Fg;
        _compare.FlatAppearance.BorderColor = Theme.Accent;

        Theme.ApplyTextColors(this);
    }

    private static DateTimePicker Picker(DateTime value) => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd HH:mm",
        Width = 150,
        Value = value,
    };

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };

    private async Task CompareAsync()
    {
        var a = new RecordComparison.Period(_aFrom.Value, _aTo.Value);
        var b = new RecordComparison.Period(_bFrom.Value, _bTo.Value);
        _compare.Enabled = false;
        try
        {
            _result.Text = await Task.Run(() => RecordComparison.Build(_resources, _activity, a, b));
        }
        finally
        {
            _compare.Enabled = true;
        }
    }
}
