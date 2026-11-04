using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace TaskMonitor;

// 직접 그리는 표. 화면에 보이는 행과 열만 그린다.
// DataGridView는 다시 그릴 때 모든 칸의 상태를 계산해서 느렸기 때문에 이 컨트롤로 바꿨다.
internal sealed class RowGrid : Control
{
    private const int RowHeight = 28;
    private const int TextInset = 6;
    private const int IconSize = 16;
    private const int IconSlot = 22;
    private const int DefaultHeaderHeight = 32;

    private static Color GridLine => Theme.Grid;
    private static Color SelectionColor => Theme.RowSel;
    private static readonly Font BodyFont = new("Segoe UI Variable Text", 9F);
    private static readonly Font BigFont = new("Segoe UI Variable Display", 15F);
    private static readonly Pen LinePen = new(GridLine);
    private static readonly Dictionary<Color, SolidBrush> BrushCache = new();

    private readonly VScrollBar _vbar = new() { Dock = DockStyle.Right, Minimum = 0, SmallChange = 1 };
    private readonly HScrollBar _hbar = new() { Dock = DockStyle.Bottom, Minimum = 0, SmallChange = 20 };
    private readonly List<ColumnDef> _columns = new();
    private readonly List<int> _x = new();
    private readonly List<int> _w = new();
    private readonly Dictionary<string, (string Text, Color Color)> _big = new();
    private IReadOnlyList<Row> _rows = Array.Empty<Row>();
    private int _selected = -1;
    private int _totalWidth;
    private int _headerHeight = DefaultHeaderHeight;
    private string? _sortKey;
    private bool _sortDesc;

    public RowGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Theme.Bg;
        Controls.Add(_vbar);
        Controls.Add(_hbar);
        _vbar.ValueChanged += (_, _) => Invalidate();
        _hbar.ValueChanged += (_, _) => Invalidate();
        Resize += (_, _) => UpdateScrollBars();
    }

    public event EventHandler? SelectionChanged;

    // 사용자가 행을 누른 경우: (행 번호, 누른 열의 키)
    public event Action<int, string>? RowClicked;

    // 사용자가 행을 우클릭한 경우: (행 번호, 컨트롤 안의 위치)
    public event Action<int, Point>? RowRightClicked;

    public event Action<string>? HeaderClicked;

    // 묶음 행의 펼침 상태(펼친 묶음의 키 모음). 글자 앞의 ▸/▾ 표시에 쓴다.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HashSet<string>? Expanded { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int HeaderHeight
    {
        get => _headerHeight;
        set
        {
            if (_headerHeight == value)
            {
                return;
            }

            _headerHeight = value;
            UpdateScrollBars();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SortKey
    {
        get => _sortKey;
        set
        {
            if (_sortKey == value)
            {
                return;
            }

            _sortKey = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SortDesc
    {
        get => _sortDesc;
        set
        {
            if (_sortDesc == value)
            {
                return;
            }

            _sortDesc = value;
            Invalidate();
        }
    }

    // 프로그램이 선택을 바꿀 때 쓴다(이벤트는 발생하지 않는다).
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int next = value >= 0 && value < _rows.Count ? value : -1;
            if (next == _selected)
            {
                return;
            }

            _selected = next;
            EnsureVisible(next);
            Invalidate();
        }
    }

    public Row? RowAt(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;

    // 자체 검증용: 화면에 보이는 행 수(접힌 묶음은 제외된 상태)와 머리글 높이
    internal int RowCount => _rows.Count;
    internal int HeaderHeightForTest => _headerHeight;

    public void SetColumns(IReadOnlyList<ColumnDef> columns)
    {
        _columns.Clear();
        _columns.AddRange(columns);
        UpdateScrollBars();
        Invalidate();
    }

    // repaint이 false면 행 목록 참조만 바꾸고 다시 그리지 않는다(화면에 보이는 내용이 같을 때).
    public void SetRows(IReadOnlyList<Row> rows, bool repaint)
    {
        _rows = rows;
        if (_selected >= rows.Count)
        {
            _selected = -1;
        }

        UpdateScrollBars();
        if (repaint)
        {
            Invalidate();
        }
    }

    public void SetBig(string key, string text, Color color)
    {
        if (_big.TryGetValue(key, out var old) && old.Text == text && old.Color == color)
        {
            return;
        }

        _big[key] = (text, color);
        Invalidate(new Rectangle(0, 0, Width, _headerHeight));
    }

    private int ContentWidth => Width - (_vbar.Visible ? _vbar.Width : 0);

    private int ContentHeight => Height - (_hbar.Visible ? _hbar.Height : 0);

    private int VisibleRowCount => Math.Max(1, (ContentHeight - _headerHeight) / RowHeight);

    private void EnsureVisible(int index)
    {
        if (!_vbar.Visible || index < 0)
        {
            return;
        }

        int first = _vbar.Value;
        int visible = VisibleRowCount;
        if (index < first)
        {
            SetVerticalValue(index);
        }
        else if (index >= first + visible)
        {
            SetVerticalValue(index - visible + 1);
        }
    }

    private void SetVerticalValue(int value)
    {
        if (!_vbar.Visible)
        {
            return;
        }

        _vbar.Value = Math.Clamp(value, 0, Math.Max(0, _rows.Count - VisibleRowCount));
    }

    // 행 수와 열 너비로 스크롤바 범위를 다시 계산한다.
    private void UpdateScrollBars()
    {
        int sbw = SystemInformation.VerticalScrollBarWidth;
        int sbh = SystemInformation.HorizontalScrollBarHeight;

        // 세로 스크롤바가 필요한지 먼저 보고, 가로 스크롤바가 생기면 높이가 줄어 한 번 더 확인한다.
        bool vNeeded = _rows.Count > Math.Max(1, (Height - _headerHeight) / RowHeight);
        // 최소화나 트레이 시작처럼 창이 스크롤바보다 좁을 때도 폭이 음수가 되지 않게 한다(스크롤바 LargeChange가 음수면 예외).
        int cw = Math.Max(1, Width - (vNeeded ? sbw : 0));
        LayoutColumns(cw);
        bool hNeeded = _totalWidth > cw;
        int ch = Height - (hNeeded ? sbh : 0);
        int visible = Math.Max(1, (ch - _headerHeight) / RowHeight);
        bool vFinal = _rows.Count > visible;
        if (vFinal != vNeeded)
        {
            cw = Math.Max(1, Width - (vFinal ? sbw : 0));
            LayoutColumns(cw);
            hNeeded = _totalWidth > cw;
        }

        _vbar.Visible = vFinal;
        if (vFinal)
        {
            _vbar.Maximum = Math.Max(0, _rows.Count - 1);
            _vbar.LargeChange = visible;
            _vbar.Value = Math.Clamp(_vbar.Value, 0, Math.Max(0, _rows.Count - visible));
        }
        else
        {
            _vbar.Value = 0;
        }

        _hbar.Visible = hNeeded;
        if (hNeeded)
        {
            _hbar.Maximum = _totalWidth - 1;
            _hbar.LargeChange = cw;
            _hbar.Value = Math.Clamp(_hbar.Value, 0, Math.Max(0, _totalWidth - cw));
        }
        else
        {
            _hbar.Value = 0;
        }
    }

    // 열의 시작 위치와 너비를 정한다. 채움 열은 남는 폭을 모두 쓰고, 전체 폭은 가로 스크롤 범위가 된다.
    private void LayoutColumns(int contentWidth)
    {
        _x.Clear();
        _w.Clear();
        int fixedTotal = _columns.Where(c => !c.Fill).Sum(c => c.Width);
        int x = 0;
        foreach (var column in _columns)
        {
            int width = column.Fill ? Math.Max(column.Width, contentWidth - fixedTotal) : column.Width;
            _x.Add(x);
            _w.Add(width);
            x += width;
        }

        _totalWidth = x;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);

        int cw = ContentWidth;
        int ch = ContentHeight;
        g.SetClip(new Rectangle(0, 0, cw, ch));

        int xOffset = _hbar.Visible ? _hbar.Value : 0;
        int first = _vbar.Visible ? _vbar.Value : 0;

        for (int c = 0; c < _columns.Count; c++)
        {
            int x = _x[c] - xOffset;
            if (x + _w[c] <= 0 || x >= cw)
            {
                continue;
            }

            DrawHeader(g, c, new Rectangle(x, 0, _w[c], _headerHeight));
        }

        g.DrawLine(LinePen, 0, _headerHeight - 1, cw, _headerHeight - 1);

        // 화면에 걸친 행만 그린다.
        int last = Math.Min(_rows.Count, first + Math.Max(1, (ch - _headerHeight) / RowHeight) + 1);
        for (int r = first; r < last; r++)
        {
            DrawRow(g, r, _headerHeight + (r - first) * RowHeight, xOffset, cw);
        }
    }

    private void DrawHeader(Graphics g, int c, Rectangle rect)
    {
        var column = _columns[c];
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
            | (column.Right ? TextFormatFlags.Right : TextFormatFlags.Left);

        if (column.Big && _big.TryGetValue(column.Key, out var big))
        {
            TextRenderer.DrawText(g, big.Text, BigFont, new Rectangle(rect.X + TextInset, rect.Y + 4, rect.Width - TextInset * 2, 36),
                big.Color, flags | TextFormatFlags.Top);
        }

        string label = Strings.Localize(column.Header) + (column.Key == _sortKey ? (_sortDesc ? " ▼" : " ▲") : "");
        int shift = c == 0 ? IconSlot : 0;
        var labelRect = column.Big
            ? new Rectangle(rect.X + TextInset, rect.Bottom - 26, rect.Width - TextInset * 2, 22)
            : new Rectangle(rect.X + TextInset + shift, rect.Y, rect.Width - TextInset * 2 - shift, rect.Height);
        var labelFlags = flags | (column.Big ? TextFormatFlags.Bottom : TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, label, BodyFont, labelRect, Theme.Fg, labelFlags);
    }

    private void DrawRow(Graphics g, int index, int y, int xOffset, int cw)
    {
        var row = _rows[index];
        bool selected = index == _selected;
        g.FillRectangle(selected ? BrushFor(SelectionColor) : BrushFor(Theme.Bg), new Rectangle(0, y, cw, RowHeight));

        for (int c = 0; c < _columns.Count; c++)
        {
            int x = _x[c] - xOffset;
            if (x + _w[c] <= 0 || x >= cw)
            {
                continue;
            }

            var column = _columns[c];
            if (!selected && row.Heat.TryGetValue(column.Key, out var heat))
            {
                g.FillRectangle(BrushFor(heat), new Rectangle(x, y, _w[c], RowHeight));
            }

            string text = row.Cells.GetValueOrDefault(column.Key) ?? "";
            if (c == 0 && row.Children is not null)
            {
                text = (Expanded?.Contains(row.Key) == true ? "▾ " : "▸ ") + text;
            }

            var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | (column.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            int textX = x + TextInset;
            if (c == 0)
            {
                // 첫 열: 아이콘 자리를 항상 비워 둬서, 아이콘이 없는 행과도 글자 위치가 맞는다.
                if (row.Icon is not null)
                {
                    g.DrawImage(row.Icon, textX, y + (RowHeight - IconSize) / 2, IconSize, IconSize);
                }

                textX += IconSlot;
            }

            TextRenderer.DrawText(g, text, BodyFont, new Rectangle(textX, y, x + _w[c] - TextInset - textX, RowHeight),
                row.Fore ?? Theme.Fg, flags);
        }

        g.DrawLine(LinePen, 0, y + RowHeight - 1, cw, y + RowHeight - 1);
    }

    private static SolidBrush BrushFor(Color color)
    {
        if (!BrushCache.TryGetValue(color, out var brush))
        {
            brush = new SolidBrush(color);
            BrushCache[color] = brush;
        }

        return brush;
    }

    private int ColumnAt(int x)
    {
        int absolute = x + (_hbar.Visible ? _hbar.Value : 0);
        for (int c = 0; c < _columns.Count; c++)
        {
            if (absolute >= _x[c] && absolute < _x[c] + _w[c])
            {
                return c;
            }
        }

        return -1;
    }

    private int RowIndexAt(int y)
    {
        int first = _vbar.Visible ? _vbar.Value : 0;
        return first + (y - _headerHeight) / RowHeight;
    }

    private void SelectByUser(int index)
    {
        if (index == _selected)
        {
            return;
        }

        SelectedIndex = index;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.Y < _headerHeight)
        {
            int headerColumn = ColumnAt(e.X);
            if (e.Button == MouseButtons.Left && headerColumn >= 0)
            {
                HeaderClicked?.Invoke(_columns[headerColumn].Key);
            }

            return;
        }

        int index = RowIndexAt(e.Y);
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            SelectByUser(index);
            int column = ColumnAt(e.X);
            RowClicked?.Invoke(index, column >= 0 ? _columns[column].Key : "");
        }
        else if (e.Button == MouseButtons.Right)
        {
            SelectByUser(index);
            RowRightClicked?.Invoke(index, e.Location);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_vbar.Visible)
        {
            return;
        }

        SetVerticalValue(_vbar.Value - e.Delta / 120 * 3);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rows.Count == 0)
        {
            return;
        }

        int current = _selected < 0 ? 0 : _selected;
        int next = e.KeyCode switch
        {
            Keys.Up => current - 1,
            Keys.Down => current + 1,
            Keys.PageUp => current - VisibleRowCount,
            Keys.PageDown => current + VisibleRowCount,
            Keys.Home => 0,
            Keys.End => _rows.Count - 1,
            _ => -1,
        };
        if (e.KeyCode is not (Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End))
        {
            return;
        }

        SelectByUser(Math.Clamp(next, 0, _rows.Count - 1));
        e.Handled = true;
    }
}
