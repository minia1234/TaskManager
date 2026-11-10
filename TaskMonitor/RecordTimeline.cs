using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TaskMonitor;

// 기록 타임라인: 자원 기록(CPU·메모리·GPU, 5초마다)을 시간축 그래프로 그리고, 경고와 게임 세션을 같은 축에 표시한다.
// 그래프를 누르면 그 시각에 가장 가까운 이벤트를 아래 목록에서 고른다. 목록에서 고르면 그래프에 그 시각을 표시한다.
internal sealed class RecordTimelineForm : Form
{
    private static readonly (string Label, TimeSpan Span)[] Ranges =
    {
        ("1시간", TimeSpan.FromHours(1)), ("6시간", TimeSpan.FromHours(6)), ("24시간", TimeSpan.FromDays(1)), ("7일", TimeSpan.FromDays(7)),
    };

    private readonly ResourceLog _resources;
    private readonly ActivityLog _activity;
    private readonly TimelineChart _chart = new() { Dock = DockStyle.Fill };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Bottom,
        Height = 220,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
    };

    private readonly CheckBox _alertsOnly = new() { Text = Strings.Localize("경고와 게임만"), Checked = true, AutoSize = true, Padding = new Padding(12, 6, 0, 0) };
    private readonly Label _range = new() { Dock = DockStyle.Top, Height = 22, Padding = new Padding(12, 2, 0, 0) };
    private TimeSpan _span = TimeSpan.FromHours(1);
    private DateTime _end = DateTime.Now;
    private IReadOnlyList<ActivityEvent> _events = Array.Empty<ActivityEvent>();
    private int _loadVersion;

    public RecordTimelineForm(ResourceLog resources, ActivityLog activity)
    {
        _resources = resources;
        _activity = activity;
        Text = Strings.Localize("기록 타임라인");
        Width = 1000;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 6, 8, 0) };
        top.Controls.Add(Button("◀", () => Shift(-1)));
        foreach (var (label, span) in Ranges)
        {
            top.Controls.Add(Button(label, () =>
            {
                _span = span;
                _end = DateTime.Now;
                Reload();
            }));
        }

        top.Controls.Add(Button("▶", () => Shift(1)));
        top.Controls.Add(_alertsOnly);

        _list.Columns.Add(Strings.Localize("시간"), 140);
        _list.Columns.Add(Strings.Localize("종류"), 70);
        _list.Columns.Add(Strings.Localize("대상"), 200);
        _list.Columns.Add(Strings.Localize("내용"), 540);
        _list.BackColor = Theme.Bg;
        _list.ForeColor = Theme.Fg;
        _list.SelectedIndexChanged += (_, _) =>
            _chart.Marker = _list.SelectedItems.Count > 0 ? ((ActivityEvent)_list.SelectedItems[0].Tag!).Time : null;
        _chart.TimeClicked += SelectNearest;
        _alertsOnly.CheckedChanged += (_, _) => FillList();

        Controls.Add(_chart);
        Controls.Add(_list);
        Controls.Add(_range);
        Controls.Add(top);
        Theme.ApplyTextColors(this);
        Load += (_, _) => Reload();
    }

    private Button Button(string text, Action click)
    {
        var button = new Button
        {
            Text = Strings.Localize(text),
            AutoSize = true,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Input,
            ForeColor = Theme.Fg,
        };
        button.FlatAppearance.BorderColor = Theme.Accent;
        button.Click += (_, _) => click();
        return button;
    }

    private void Shift(int direction)
    {
        var end = _end + direction * _span;
        _end = end > DateTime.Now ? DateTime.Now : end;
        Reload();
    }

    // 기록 파일은 최대 수십 MB라 백그라운드에서 읽는다. 그 사이 다른 구간을 고르면 늦게 끝난 결과는 버린다.
    private async void Reload()
    {
        int version = ++_loadVersion;
        DateTime from = _end - _span, to = _end;
        _range.Text = $"{from:MM-dd HH:mm} ~ {to:MM-dd HH:mm}   " + Strings.Localize("읽는 중...");
        IReadOnlyList<ResourceSample> samples;
        IReadOnlyList<ActivityEvent> events;
        try
        {
            (samples, events) = await Task.Run(() => (_resources.ReadRange(from, to), _activity.ReadRange(from, to)));
        }
        catch (IOException ex)
        {
            // 기록 파일이 막 회전(.1로 이동)되는 순간이면 못 읽을 수 있다. 다시 누르면 된다.
            _range.Text = ex.Message;
            return;
        }

        if (version != _loadVersion || IsDisposed)
        {
            return;
        }

        _events = events;
        _range.Text = $"{from:MM-dd HH:mm} ~ {to:MM-dd HH:mm}   " + Strings.Fmt("자원 기록 {0}개, 이벤트 {1}개", samples.Count, events.Count);
        _chart.SetData(from, to, samples, events.Where(IsMarked).ToList());
        FillList();
    }

    private static bool IsMarked(ActivityEvent e) => e.Kind is ActivityLog.Alert or ActivityLog.Game;

    private void FillList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _events.Where(e => !_alertsOnly.Checked || IsMarked(e)).Reverse().Take(5000))
        {
            var item = new ListViewItem(new[] { e.Time.ToString("MM-dd HH:mm:ss"), RowBuilders.KindLabel(e.Kind), Strings.Localize(e.Subject), e.Detail }) { Tag = e };
            if (e.Kind == ActivityLog.Alert)
            {
                item.ForeColor = Theme.Red;
            }

            _list.Items.Add(item);
        }

        _list.EndUpdate();
    }

    private void SelectNearest(DateTime time)
    {
        ListViewItem? best = null;
        foreach (ListViewItem item in _list.Items)
        {
            if (best is null || Math.Abs((((ActivityEvent)item.Tag!).Time - time).Ticks) < Math.Abs((((ActivityEvent)best.Tag!).Time - time).Ticks))
            {
                best = item;
            }
        }

        _chart.Marker = time;
        if (best is not null)
        {
            _list.SelectedItems.Clear();
            best.Selected = true;
            best.EnsureVisible();
        }
    }
}

// 시간축 그래프. 화면 가로 1픽셀마다 그 구간의 최댓값을 그린다(7일이면 12만 개가 넘어서 그대로 그리면 느리다).
// 기록이 15초 넘게 끊긴 곳(앱이 꺼져 있던 때)은 선을 잇지 않는다.
internal sealed class TimelineChart : Control
{
    private static readonly (string Label, Color Color, Func<ResourceSample, double> Value)[] Series =
    {
        ("CPU", ColorTranslator.FromHtml("#0078D4"), s => s.CpuPercent),
        ("메모리", ColorTranslator.FromHtml("#8764B8"), s => s.RamPercent),
        ("GPU", ColorTranslator.FromHtml("#D83B01"), s => s.GpuPercent),
    };

    private const int PadLeft = 44, PadRight = 12, PadTop = 28, PadBottom = 26;
    private DateTime _from, _to;
    private IReadOnlyList<ResourceSample> _samples = Array.Empty<ResourceSample>();
    private IReadOnlyList<ActivityEvent> _marks = Array.Empty<ActivityEvent>();
    private DateTime? _marker;
    private int _hoverX = -1;

    public event Action<DateTime>? TimeClicked;

    public TimelineChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Bg;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DateTime? Marker
    {
        get => _marker;
        set
        {
            _marker = value;
            Invalidate();
        }
    }

    public void SetData(DateTime from, DateTime to, IReadOnlyList<ResourceSample> samples, IReadOnlyList<ActivityEvent> marks)
    {
        (_from, _to, _samples, _marks, _marker) = (from, to, samples, marks, null);
        Invalidate();
    }

    private Rectangle Plot => new(PadLeft, PadTop, Math.Max(1, Width - PadLeft - PadRight), Math.Max(1, Height - PadTop - PadBottom));

    private float X(DateTime t) => Plot.Left + (float)((t - _from).Ticks / (double)Math.Max(1, (_to - _from).Ticks) * Plot.Width);

    private DateTime TimeAt(int x) => _from + TimeSpan.FromTicks((long)((x - Plot.Left) / (double)Plot.Width * (_to - _from).Ticks));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = Plot.Contains(e.Location) ? e.X : -1;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverX = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (Plot.Contains(e.Location))
        {
            TimeClicked?.Invoke(TimeAt(e.X));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = Plot;
        using var font = new Font("Segoe UI", 8.5F);
        using var grid = new Pen(Theme.Grid);
        using var text = new SolidBrush(Theme.Fg2);

        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            float y = plot.Bottom - pct / 100f * plot.Height;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{pct}%", font, text, 4, y - 7);
        }

        if (_to <= _from)
        {
            return;
        }

        // 시간 눈금: 구간 길이에 맞춰 5~8칸.
        var span = _to - _from;
        string format = span > TimeSpan.FromDays(1) ? "MM-dd HH:mm" : "HH:mm";
        for (int i = 0; i <= 6; i++)
        {
            var t = _from + span * i / 6;
            float x = X(t);
            g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            var size = g.MeasureString(t.ToString(format), font);
            g.DrawString(t.ToString(format), font, text, Math.Clamp(x - size.Width / 2, 0, Width - size.Width), plot.Bottom + 4);
        }

        // 경고(빨강)와 게임 세션(초록)은 그래프 위쪽에 짧은 세로선으로 표시한다.
        foreach (var mark in _marks)
        {
            using var pen = new Pen(mark.Kind == ActivityLog.Alert ? Theme.Red : Color.SeaGreen, 2);
            float x = X(mark.Time);
            g.DrawLine(pen, x, plot.Top - 10, x, plot.Top + 6);
        }

        float legendX = plot.Left;
        foreach (var (label, color, value) in Series)
        {
            DrawSeries(g, plot, color, value);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, legendX, 6, 10, 10);
            g.DrawString(Strings.Localize(label), font, text, legendX + 13, 3);
            legendX += 80;
        }

        if (_marker is { } marker)
        {
            using var pen = new Pen(Theme.Accent, 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, X(marker), plot.Top, X(marker), plot.Bottom);
        }

        if (_hoverX >= 0)
        {
            DrawHover(g, plot, font);
        }
    }

    private void DrawSeries(Graphics g, Rectangle plot, Color color, Func<ResourceSample, double> value)
    {
        using var pen = new Pen(color, 1.5f);
        var gap = ResourceLog.Interval * 3;
        PointF? previous = null;
        DateTime previousTime = default;
        int bucket = int.MinValue;
        double peak = 0;
        DateTime bucketTime = default;

        void Flush()
        {
            if (bucket == int.MinValue)
            {
                return;
            }

            var point = new PointF(bucket, plot.Bottom - (float)(Math.Clamp(peak, 0, 100) / 100 * plot.Height));
            if (previous is { } p && bucketTime - previousTime <= gap + TimeSpan.FromTicks((_to - _from).Ticks / plot.Width))
            {
                g.DrawLine(pen, p, point);
            }

            previous = point;
            previousTime = bucketTime;
        }

        foreach (var s in _samples)
        {
            int x = (int)X(s.Time);
            if (x != bucket)
            {
                Flush();
                bucket = x;
                peak = 0;
            }

            peak = Math.Max(peak, value(s));
            bucketTime = s.Time;
        }

        Flush();
    }

    private void DrawHover(Graphics g, Rectangle plot, Font font)
    {
        var time = TimeAt(_hoverX);
        using var pen = new Pen(Theme.Fg3);
        g.DrawLine(pen, _hoverX, plot.Top, _hoverX, plot.Bottom);

        // 가장 가까운 기록 값을 보여 준다. 30초 안에 기록이 없으면 "기록 없음".
        var nearest = _samples.Count == 0 ? null : _samples.MinBy(s => Math.Abs((s.Time - time).Ticks));
        string label = nearest is not null && Math.Abs((nearest.Time - time).TotalSeconds) <= 30
            ? $"{nearest.Time:MM-dd HH:mm:ss}\nCPU {nearest.CpuPercent:F0}%   {Strings.Localize("메모리")} {nearest.RamPercent:F0}%   GPU {nearest.GpuPercent:F0}%"
            : $"{time:MM-dd HH:mm:ss}\n{Strings.Localize("기록 없음")}";
        var window = TimeSpan.FromTicks((_to - _from).Ticks / plot.Width * 4);
        foreach (var mark in _marks.Where(m => (m.Time - time).Duration() <= window).Take(3))
        {
            label += $"\n{Strings.Localize(mark.Subject)}: {mark.Detail}";
        }

        var size = g.MeasureString(label, font);
        float x = _hoverX + 12 + size.Width > Width ? _hoverX - 12 - size.Width : _hoverX + 12;
        var box = new RectangleF(x, plot.Top + 8, size.Width + 8, size.Height + 6);
        using var back = new SolidBrush(Theme.Input);
        using var border = new Pen(Theme.Border);
        using var fore = new SolidBrush(Theme.Fg);
        g.FillRectangle(back, box);
        g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
        g.DrawString(label, font, fore, box.X + 4, box.Y + 3);
    }
}
