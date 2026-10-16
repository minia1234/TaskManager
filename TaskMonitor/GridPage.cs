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

    // 첫 열 앞에 그리는 프로그램 아이콘(없으면 null)
    public Bitmap? Icon { get; set; }
}

// 작업 관리자 표 공통 페이지: 정렬, 검색, 묶음 펼치기, 히트맵, 선택 유지, 열 머리글의 큰 사용률 숫자.
// 실제 그리기는 RowGrid가 맡는다(보이는 행만 그린다).
internal sealed class GridPage : UserControl
{
    private readonly RowGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly ToolStrip _toolbar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private readonly List<ColumnDef> _all;
    private readonly Dictionary<string, ColumnDef> _defs;
    private readonly List<string> _visible = new();
    private readonly HashSet<string> _expanded = new();
    private IReadOnlyList<Row> _rows = Array.Empty<Row>();
    private List<Row> _shown = new();
    private string _filter = "";
    private string? _sortKey;
    private bool _sortDesc;
    private string? _selectedKey;
    private int _contentHash;

    public GridPage(IReadOnlyList<ColumnDef> all, IEnumerable<string> visible)
    {
        _all = all.ToList();
        _defs = all.ToDictionary(c => c.Key);

        _grid.Expanded = _expanded;
        _grid.SelectionChanged += (_, _) =>
        {
            _selectedKey = SelectedRow?.Key;
            SelectionChangedByUser?.Invoke();
        };
        _grid.RowClicked += OnRowClicked;
        _grid.RowRightClicked += OnRowRightClicked;
        _grid.HeaderClicked += OnHeaderClicked;

        Controls.Add(_grid);
        Controls.Add(_toolbar);
        SetColumns(visible);
    }

    public event Action? SelectionChangedByUser;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Row, ContextMenuStrip?>? MenuFactory { get; set; }

    public ToolStrip Toolbar => _toolbar;

    // 자체 검증용: 내부 표 컨트롤
    internal RowGrid Grid => _grid;

    public IReadOnlyList<ColumnDef> AllColumns => _all;

    public IReadOnlyList<string> VisibleColumns => _visible;

    public Row? SelectedRow => _grid.RowAt(_grid.SelectedIndex);

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

        _grid.SetColumns(_visible.Select(k => _defs[k]).ToList());
        _grid.HeaderHeight = _visible.Any(k => _defs[k].Big) ? 64 : 32;
        _contentHash = 0;
        Rebuild();
    }

    public void SetRows(IReadOnlyList<Row> rows)
    {
        _rows = rows;
        Rebuild();
    }

    public void SetFilter(string text)
    {
        _filter = text.Trim();
        Rebuild();
    }

    // 열 머리글에 크게 표시할 값 (예: CPU 전체 사용률)
    public void SetBig(string key, string text, Color color)
    {
        _grid.SetBig(key, text, color);
    }

    public void SelectKey(string key)
    {
        _selectedKey = key;
        Rebuild();
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

        Rebuild();
    }

    private void OnRowClicked(int index, string key)
    {
        var row = _grid.RowAt(index);
        if (row?.Children is null || _visible.Count == 0 || key != _visible[0])
        {
            return;
        }

        // 묶음 행의 첫 열을 누르면 펼치고 접는다.
        if (!_expanded.Remove(row.Key))
        {
            _expanded.Add(row.Key);
        }

        Rebuild();
    }

    private void OnRowRightClicked(int index, Point point)
    {
        var row = _grid.RowAt(index);
        if (row is null)
        {
            return;
        }

        MenuFactory?.Invoke(row)?.Show(_grid, point);
    }

    private void OnHeaderClicked(string key)
    {
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

        Rebuild();
    }

    // 필터, 정렬, 펼치기를 반영해 표시 목록을 다시 만든다. 내용이 같으면 그리기는 건너뛴다.
    private void Rebuild()
    {
        var shown = new List<Row>();
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

            shown.Add(row);
            if (open)
            {
                shown.AddRange(children);
            }
        }

        _shown = shown;
        int hash = ContentHash();
        bool changed = hash != _contentHash;
        _contentHash = hash;

        _grid.SortKey = _sortKey;
        _grid.SortDesc = _sortDesc;
        _grid.SetRows(_shown, changed);
        _grid.SelectedIndex = _selectedKey is null ? -1 : _shown.FindIndex(r => r.Key == _selectedKey);
    }

    // 표시 중인 행과 열의 글자, 펼침 상태를 해시로 요약한다. 이전과 같으면 다시 그릴 필요가 없다.
    private int ContentHash()
    {
        var hash = new HashCode();
        hash.Add(_visible.Count);
        foreach (var row in _shown)
        {
            hash.Add(row.Key);
            hash.Add(row.Icon is not null);
            if (row.Children is not null)
            {
                hash.Add(_expanded.Contains(row.Key));
            }

            foreach (var key in _visible)
            {
                hash.Add(row.Cells.GetValueOrDefault(key));
            }
        }

        return hash.ToHashCode();
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
