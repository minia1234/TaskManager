using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace TaskMonitor;

internal sealed record ColumnDef(string Key, string Header, int Width, bool Right = false, bool Fill = false, bool Big = false);

// 표의 한 행. 키(Key)로 선택과 펼침 상태를 기억한다.
// Children이 있으면 묶음 행이다(펼치기/접기 대상).
internal sealed class Row
{
    public required string Key { get; init; }
    public Dictionary<string, string> Cells { get; } = new();
    public Dictionary<string, IComparable?> Sort { get; } = new();
    public Dictionary<string, Color> Heat { get; } = new();
    public Color? Fore { get; set; }
    public object? Tag { get; set; }
    public List<Row>? Children { get; set; }
}

// 작업 관리자 표 공통 페이지: 정렬, 검색, 묶음 펼치기, 히트맵, 선택 유지, 열 머리글의 큰 사용률 숫자.
internal sealed class GridPage : UserControl
{
    private static readonly Color GridLine = ColorTranslator.FromHtml("#E5E5E5");
    private static readonly Color SelectionColor = ColorTranslator.FromHtml("#CCE8FF");
    private static readonly Font BodyFont = new("Segoe UI Variable Text", 9F);
    private static readonly Font BigFont = new("Segoe UI Variable Display", 15F);

    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill };
    private readonly ToolStrip _toolbar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private readonly List<ColumnDef> _all;
    private readonly Dictionary<string, ColumnDef> _defs;
    private readonly List<string> _visible = new();
    private readonly HashSet<string> _expanded = new();
    private readonly Dictionary<string, (string Text, Color Color)> _big = new();
    private IReadOnlyList<Row> _rows = Array.Empty<Row>();
    private string _filter = "";
    private string? _sortKey;
    private bool _sortDesc;
    private string? _selectedKey;
    private bool _rendering;

    public GridPage(IReadOnlyList<ColumnDef> all, IEnumerable<string> visible)
    {
        _all = all.ToList();
        _defs = all.ToDictionary(c => c.Key);

        ConfigureGrid();
        Controls.Add(_grid);
        Controls.Add(_toolbar);
        SetColumns(visible);
    }

    public event Action? SelectionChangedByUser;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Row, ContextMenuStrip?>? MenuFactory { get; set; }

    public ToolStrip Toolbar => _toolbar;

    public DataGridView Grid => _grid;

    public IReadOnlyList<ColumnDef> AllColumns => _all;

    public IReadOnlyList<string> VisibleColumns => _visible;

    public Row? SelectedRow => _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as Row : null;

    public IEnumerable<Row> GroupRows => _rows.Where(r => r.Children is not null);

    public void SetColumns(IEnumerable<string> keys)
    {
        _visible.Clear();
        foreach (var key in keys)
        {
            if (_defs.ContainsKey(key) && !_visible.Contains(key))
            {
                _visible.Add(key);
            }
        }

        if (_visible.Count == 0)
        {
            _visible.Add(_all[0].Key);
        }

        _grid.Columns.Clear();
        foreach (var key in _visible)
        {
            var d = _defs[key];
            var column = new DataGridViewTextBoxColumn
            {
                Name = key,
                HeaderText = d.Header,
                Width = d.Width,
                SortMode = DataGridViewColumnSortMode.Programmatic,
            };
            column.DefaultCellStyle.Alignment = d.Right
                ? DataGridViewContentAlignment.MiddleRight
                : DataGridViewContentAlignment.MiddleLeft;
            if (d.Fill)
            {
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            }

            _grid.Columns.Add(column);
        }

        _grid.ColumnHeadersHeight = _visible.Any(k => _defs[k].Big) ? 64 : 32;
        Render();
    }

    public void SetRows(IReadOnlyList<Row> rows)
    {
        _rows = rows;
        Render();
    }

    public void SetFilter(string text)
    {
        _filter = text.Trim();
        Render();
    }

    // 열 머리글에 크게 표시할 값 (예: CPU 전체 사용률)
    public void SetBig(string key, string text, Color color)
    {
        _big[key] = (text, color);
        _grid.Invalidate();
    }

    public void SelectKey(string key)
    {
        _selectedKey = key;
        Render();
    }

    public void ExpandAll(bool open)
    {
        _expanded.Clear();
        if (open)
        {
            foreach (var group in GroupRows)
            {
                _expanded.Add(group.Key);
            }
        }

        Render();
    }

    private void ConfigureGrid()
    {
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.RowTemplate.Height = 28;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.GridColor = GridLine;
        _grid.DefaultCellStyle.Font = BodyFont;
        _grid.DefaultCellStyle.ForeColor = Color.Black;
        _grid.DefaultCellStyle.SelectionBackColor = SelectionColor;
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);

        _grid.CellPainting += PaintHeaderCell;
        _grid.ColumnHeaderMouseClick += OnHeaderClick;
        _grid.CellClick += OnCellClick;
        _grid.SelectionChanged += (_, _) =>
        {
            if (_rendering)
            {
                return;
            }

            _selectedKey = SelectedRow?.Key;
            SelectionChangedByUser?.Invoke();
        };
        _grid.MouseDown += OnMouseDown;
    }

    // 열 머리글: 큰 숫자 + 열 이름, 정렬 중이면 ▲/▼ 표시
    private void PaintHeaderCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex != -1 || e.Graphics is not { } g || e.ColumnIndex < 0 || e.ColumnIndex >= _visible.Count)
        {
            return;
        }

        e.Handled = true;
        var bounds = e.CellBounds;
        g.FillRectangle(Brushes.White, bounds);

        string key = _visible[e.ColumnIndex];
        var d = _defs[key];
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | (d.Right ? TextFormatFlags.Right : TextFormatFlags.Left);

        if (d.Big && _big.TryGetValue(key, out var big))
        {
            TextRenderer.DrawText(g, big.Text, BigFont, new Rectangle(bounds.X + 6, bounds.Y + 4, bounds.Width - 12, 36),
                big.Color, flags | TextFormatFlags.Top);
        }

        string label = d.Header + (key == _sortKey ? (_sortDesc ? " ▼" : " ▲") : "");
        var labelRect = d.Big
            ? new Rectangle(bounds.X + 6, bounds.Bottom - 26, bounds.Width - 12, 22)
            : new Rectangle(bounds.X + 6, bounds.Y, bounds.Width - 12, bounds.Height);
        var labelFlags = flags | (d.Big ? TextFormatFlags.Bottom : TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, label, BodyFont, labelRect, Color.Black, labelFlags);

        using var pen = new Pen(GridLine);
        g.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
    }

    private void OnHeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex != -1 || e.ColumnIndex < 0 || e.ColumnIndex >= _visible.Count)
        {
            return;
        }

        string key = _visible[e.ColumnIndex];
        if (_sortKey == key)
        {
            _sortDesc = !_sortDesc;
        }
        else
        {
            _sortKey = key;
            // 숫자 열은 큰 값부터, 글자 열은 가나다순부터 보여준다.
            _sortDesc = _defs[key].Right;
        }

        Render();
    }

    private void OnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not Row row || row.Children is null)
        {
            return;
        }

        if (!_expanded.Remove(row.Key))
        {
            _expanded.Add(row.Key);
        }

        Render();
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        var hit = _grid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0 || _grid.Rows[hit.RowIndex].Tag is not Row row)
        {
            return;
        }

        _grid.ClearSelection();
        _grid.Rows[hit.RowIndex].Selected = true;
        var menu = MenuFactory?.Invoke(row);
        menu?.Show(_grid, e.Location);
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            int firstRow = _grid.Rows.Count > 0 && _grid.FirstDisplayedScrollingRowIndex >= 0
                ? _grid.FirstDisplayedScrollingRowIndex
                : 0;

            _grid.SuspendLayout();
            _grid.Rows.Clear();

            foreach (var row in Ordered(_rows))
            {
                bool open = row.Children is not null && _expanded.Contains(row.Key);
                var children = row.Children is null
                    ? new List<Row>()
                    : Ordered(row.Children).Where(Matches).ToList();

                if (!Matches(row) && children.Count == 0)
                {
                    continue;
                }

                AddRow(row, open);
                if (open)
                {
                    foreach (var child in children)
                    {
                        AddRow(child, false);
                    }
                }
            }

            _grid.ClearSelection();
            if (_selectedKey is not null)
            {
                foreach (DataGridViewRow gridRow in _grid.Rows)
                {
                    if (gridRow.Tag is Row r && r.Key == _selectedKey)
                    {
                        gridRow.Selected = true;
                        break;
                    }
                }
            }

            _grid.ResumeLayout();
            if (firstRow < _grid.RowCount)
            {
                _grid.FirstDisplayedScrollingRowIndex = firstRow;
            }

            _grid.Invalidate();
        }
        finally
        {
            _rendering = false;
        }
    }

    private void AddRow(Row row, bool open)
    {
        var values = new string[_visible.Count];
        for (int i = 0; i < _visible.Count; i++)
        {
            string text = row.Cells.GetValueOrDefault(_visible[i]) ?? "";
            if (i == 0 && row.Children is not null)
            {
                text = (open ? "▾ " : "▸ ") + text;
            }

            values[i] = text;
        }

        int index = _grid.Rows.Add(values);
        var gridRow = _grid.Rows[index];
        gridRow.Tag = row;
        for (int i = 0; i < _visible.Count; i++)
        {
            if (row.Heat.TryGetValue(_visible[i], out var color))
            {
                gridRow.Cells[i].Style.BackColor = color;
            }
        }

        if (row.Fore is { } fore)
        {
            gridRow.DefaultCellStyle.ForeColor = fore;
        }
    }

    private IEnumerable<Row> Ordered(IEnumerable<Row> rows)
    {
        if (_sortKey is null)
        {
            return rows;
        }

        string key = _sortKey;
        // 값이 없는 행은 항상 가장 작은 값으로 본다.
        var cmp = Comparer<IComparable?>.Create((a, b) =>
            a is null ? (b is null ? 0 : -1) : b is null ? 1 : a.CompareTo(b));

        return _sortDesc
            ? rows.OrderByDescending(r => r.Sort.GetValueOrDefault(key), cmp)
            : rows.OrderBy(r => r.Sort.GetValueOrDefault(key), cmp);
    }

    private bool Matches(Row row)
    {
        if (_filter.Length == 0)
        {
            return true;
        }

        return row.Cells.Values.Any(v => v.Contains(_filter, StringComparison.OrdinalIgnoreCase));
    }
}
