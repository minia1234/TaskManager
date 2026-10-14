using System.Drawing;
using System.Windows.Forms;

namespace TaskMonitor;

// 성능 페이지: 왼쪽에 리소스 카드(CPU, 메모리, 디스크, 네트워크, GPU), 오른쪽에 선택한 리소스의 큰 그래프와 통계.
// CPU를 고르면 논리 프로세서별 그래프도 함께 보여준다.
internal sealed class PerformancePage : UserControl
{
    private static readonly string[] Names = { "CPU", "메모리", "디스크", "네트워크", "GPU" };
    private static readonly Color[] LineColors =
    {
        ColorTranslator.FromHtml("#0078D4"),
        ColorTranslator.FromHtml("#8764B8"),
        ColorTranslator.FromHtml("#107C10"),
        ColorTranslator.FromHtml("#C239B3"),
        ColorTranslator.FromHtml("#D83B01"),
    };

    private readonly List<double>[] _history = Enumerable.Range(0, Names.Length).Select(_ => new List<double>()).ToArray();
    private readonly Label[] _cardValue = new Label[Names.Length];
    private readonly GraphControl[] _cardGraph = new GraphControl[Names.Length];
    private readonly List<GraphControl> _coreGraphs = new();
    private readonly List<Label> _coreLabels = new();
    private readonly List<List<double>> _coreHistories = new();
    private readonly Label _detailTitle = new() { Dock = DockStyle.Top, Height = 40, Font = new Font("Segoe UI Variable Display", 18F) };
    private readonly Label _detailValue = new() { Dock = DockStyle.Top, Height = 44, Font = new Font("Segoe UI Variable Display", 24F) };
    private readonly Label _detailStats = new() { Dock = DockStyle.Top, Height = 64, Font = new Font("Segoe UI Variable Text", 9.5F) };
    private readonly GraphControl _detailGraph = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _cores = new() { Dock = DockStyle.Bottom, Height = 170, AutoScroll = true };
    private int _selected;
    private double[] _latest = new double[Names.Length];

    public PerformancePage()
    {
        var cards = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 250,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8),
        };

        for (int i = 0; i < Names.Length; i++)
        {
            int index = i;
            var card = new Panel { Width = 226, Height = 96, Margin = new Padding(0, 0, 0, 8), BackColor = Color.White };
            var graph = new GraphControl { Dock = DockStyle.Fill, LineColor = LineColors[i], FillColor = Tint(LineColors[i]) };
            var value = new Label { Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI Variable Text", 8.5F) };
            var title = new Label { Dock = DockStyle.Top, Height = 22, Text = Names[i], Font = new Font("Segoe UI Variable Text", 9.5F, FontStyle.Bold) };
            card.Controls.Add(graph);
            card.Controls.Add(value);
            card.Controls.Add(title);
            WireClick(card, () => Select(index));
            cards.Controls.Add(card);
            _cardValue[i] = value;
            _cardGraph[i] = graph;
        }

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 8) };
        _detailGraph.LineColor = LineColors[0];
        _detailGraph.FillColor = Tint(LineColors[0]);
        right.Controls.Add(_detailGraph);
        right.Controls.Add(_cores);
        right.Controls.Add(_detailStats);
        right.Controls.Add(_detailValue);
        right.Controls.Add(_detailTitle);

        Controls.Add(right);
        Controls.Add(cards);
        _detailTitle.Text = Names[0];
        Select(0);
    }

    public void Update(Snapshot snap, PerfSample perf)
    {
        double memUsed = perf.TotalMemoryBytes > 0
            ? 100.0 * (perf.TotalMemoryBytes - perf.AvailableMemoryBytes) / perf.TotalMemoryBytes
            : 0;

        _latest = new[] { snap.TotalCpuPercent, memUsed, perf.DiskBusyPercent, perf.NetPercent, perf.GpuPercent };
        for (int i = 0; i < Names.Length; i++)
        {
            Push(_history[i], _latest[i]);
            _cardGraph[i].SetValues(_history[i]);
        }

        EnsureCores(snap.PerCpuPercent.Length);
        for (int c = 0; c < snap.PerCpuPercent.Length; c++)
        {
            Push(_coreHistories[c], snap.PerCpuPercent[c]);
            _coreGraphs[c].SetValues(_coreHistories[c]);
            _coreLabels[c].Text = $"CPU {c}   {snap.PerCpuPercent[c]:F0}%";
        }

        _cardValue[0].Text = $"{_latest[0]:F0}%   논리 프로세서 {snap.LogicalProcessors}개";
        _cardValue[1].Text = $"{memUsed:F0}%   {Gb(perf.TotalMemoryBytes - perf.AvailableMemoryBytes):F1} / {Gb(perf.TotalMemoryBytes):F1} GB";
        _cardValue[2].Text = $"{_latest[2]:F0}%   {RowBuilders.FormatRate(perf.DiskBytesPerSec)}";
        _cardValue[3].Text = $"{RowBuilders.FormatRate(perf.NetBytesPerSec)}   {_latest[3]:F0}%";
        _cardValue[4].Text = $"{_latest[4]:F0}%";

        _detailValue.Text = _selected == 0 ? $"{_latest[0]:F1}%" : _cardValue[_selected].Text;
        _detailStats.Text = StatsFor(snap, perf, memUsed);
        _detailGraph.SetValues(_history[_selected]);
    }

    private string StatsFor(Snapshot snap, PerfSample perf, double memUsed)
    {
        return _selected switch
        {
            0 => $"프로세스 {snap.ProcessCount}개    스레드 {snap.TotalThreads}개    핸들 {snap.TotalHandles}개\r\n"
                 + $"가동 시간 {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm\\:ss}",
            1 => $"사용 중 {Gb(perf.TotalMemoryBytes - perf.AvailableMemoryBytes):F1} GB    "
                 + $"사용 가능 {Gb(perf.AvailableMemoryBytes):F1} GB    전체 {Gb(perf.TotalMemoryBytes):F1} GB",
            2 => $"사용 시간 {_latest[2]:F0}%    전송 속도 {RowBuilders.FormatRate(perf.DiskBytesPerSec)}",
            3 => $"받기·보내기 {RowBuilders.FormatRate(perf.NetBytesPerSec)}    링크 사용률 {_latest[3]:F1}%",
            _ => $"엔진 사용률 합계 {_latest[4]:F0}% (근사치)",
        };
    }

    private void Select(int index)
    {
        _selected = index;
        _detailTitle.Text = Names[index];
        _detailGraph.LineColor = LineColors[index];
        _detailGraph.FillColor = Tint(LineColors[index]);
        _cores.Visible = index == 0;
        _detailGraph.SetValues(_history[index]);
        _detailValue.Text = index == 0 ? $"{_latest[0]:F1}%" : _cardValue[index].Text;
    }

    private void EnsureCores(int count)
    {
        if (_coreGraphs.Count == count)
        {
            return;
        }

        _cores.Controls.Clear();
        _coreGraphs.Clear();
        _coreLabels.Clear();
        _coreHistories.Clear();
        for (int c = 0; c < count; c++)
        {
            var box = new Panel { Width = 150, Height = 78, Margin = new Padding(4) };
            var graph = new GraphControl { Dock = DockStyle.Fill, LineColor = LineColors[0], FillColor = Tint(LineColors[0]) };
            var label = new Label { Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI Variable Text", 8.5F) };
            box.Controls.Add(graph);
            box.Controls.Add(label);
            _cores.Controls.Add(box);
            _coreGraphs.Add(graph);
            _coreLabels.Add(label);
            _coreHistories.Add(new List<double>());
        }
    }

    private static void Push(List<double> series, double value)
    {
        series.Add(value);
        if (series.Count > GraphControl.Capacity)
        {
            series.RemoveAt(0);
        }
    }

    private static void WireClick(Control control, Action action)
    {
        control.Click += (_, _) => action();
        foreach (Control child in control.Controls)
        {
            WireClick(child, action);
        }
    }

    // 그래프 면 색: 선 색을 아주 연하게 바꾼 색
    private static Color Tint(Color c) => Color.FromArgb(64, c);

    private static double Gb(long bytes) => bytes / 1073741824.0;
}
