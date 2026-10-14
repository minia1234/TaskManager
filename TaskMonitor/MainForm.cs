using System.Drawing;
using System.Windows.Forms;

namespace TaskMonitor;

// 작업 관리자 메인 창: 메뉴, 왼쪽 탐색, 7개 페이지, 하단 상태 바.
internal sealed class MainForm : Form
{
    private const int PageProcesses = 0;
    private const int PagePerformance = 1;
    private const int PageHistory = 2;
    private const int PageStartup = 3;
    private const int PageUsers = 4;
    private const int PageDetails = 5;
    private const int PageServices = 6;

    private static readonly string[] PageTitles = { "프로세스", "성능", "앱 기록", "시작 프로그램", "사용자", "세부 정보", "서비스" };
    private static readonly int[] IntervalMs = { 500, 1000, 4000, 0 };
    private static readonly string[] IntervalNames = { "0.5 초", "1 초", "4 초", "일시 중지" };
    private static readonly TimeSpan SlowRefresh = TimeSpan.FromSeconds(3);
    private static readonly Color NavSelected = ColorTranslator.FromHtml("#E0ECF9");
    private static readonly Color NavNormal = ColorTranslator.FromHtml("#F3F3F3");
    private static readonly Color TopBarColor = ColorTranslator.FromHtml("#EDF3F8");
    private static readonly Color StatusBarColor = ColorTranslator.FromHtml("#F3F3F3");
    private static readonly Font BodyFont = new("Segoe UI Variable Text", 9F);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CpuSampler _sampler = new();
    private readonly SystemPerf _perf = new();
    private readonly ProcessInfoCache _info = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly NotifyIcon _tray = new();

    private readonly GridPage _processes;
    private readonly GridPage _details;
    private readonly GridPage _services;
    private readonly GridPage _startup;
    private readonly GridPage _users;
    private readonly PerformancePage _performance = new() { Dock = DockStyle.Fill };
    private readonly Panel _historyPage = new() { Dock = DockStyle.Fill };
    private readonly Control[] _pages;
    private readonly Button[] _nav = new Button[PageTitles.Length];
    private readonly Panel _content = new() { Dock = DockStyle.Fill };

    private readonly TextBox _search = new() { Width = 360 };
    private readonly ComboBox _interval = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly CheckBox _groupCheck = new() { Text = "이름별 합산", AutoSize = true };
    private readonly NumericUpDown _threshold = new() { Minimum = 1, Maximum = 100, Width = 60 };
    private readonly CheckBox _logCheck = new() { Text = "임계치 이상이면 기록", AutoSize = true };
    private readonly Label _pageTitle = new() { AutoSize = true, Dock = DockStyle.Left, Font = new Font("Segoe UI Variable Display", 16F) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
    private readonly MenuStrip _menu = new();

    private readonly ToolStripMenuItem _alwaysOnTop = new("항상 위에 표시") { CheckOnClick = true };
    private readonly ToolStripMenuItem _minimizeOnUse = new("사용하지 않을 때 최소화") { CheckOnClick = true };
    private readonly ToolStripMenuItem _hideWhenMinimized = new("최소화 시 숨기기") { CheckOnClick = true };
    private readonly ToolStripMenuItem _groupMenu = new("이름별 합산") { CheckOnClick = true };
    private readonly ToolStripMenuItem[] _intervalMenu = new ToolStripMenuItem[IntervalMs.Length];

    private readonly ToolStripButton _pEnd;
    private readonly ToolStripButton _pEco;
    private readonly ToolStripButton _dEnd;
    private readonly ToolStripButton _dEco;
    private readonly ToolStripButton _sStart;
    private readonly ToolStripButton _sStop;
    private readonly ToolStripButton _sRestart;
    private readonly ToolStripButton _stEnable;
    private readonly ToolStripButton _stDisable;
    private readonly ToolStripButton _uDisconnect;
    private readonly ToolStripButton _uLogoff;
    private readonly ToolStripButton _uMessage;

    private int _currentPage = PageProcesses;
    private bool _syncing;
    private Snapshot? _last;
    private PerfSample? _lastPerf;
    private DateTime _lastSlow = DateTime.MinValue;

    public MainForm()
    {
        Text = "작업 관리자";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = Color.White;

        _processes = new GridPage(RowBuilders.ProcessColumns, ColumnsFor("processes", RowBuilders.DefaultProcessColumns)) { Dock = DockStyle.Fill };
        _details = new GridPage(RowBuilders.DetailColumns, ColumnsFor("details", RowBuilders.DefaultDetailColumns)) { Dock = DockStyle.Fill };
        _services = new GridPage(RowBuilders.ServiceColumns, RowBuilders.ServiceColumns.Select(c => c.Key)) { Dock = DockStyle.Fill };
        _startup = new GridPage(RowBuilders.StartupColumns, RowBuilders.StartupColumns.Select(c => c.Key)) { Dock = DockStyle.Fill };
        _users = new GridPage(RowBuilders.UserColumns, RowBuilders.UserColumns.Select(c => c.Key)) { Dock = DockStyle.Fill };

        _historyPage.Controls.Add(new Label
        {
            Text = "앱 기록은 Windows 사용 기록(ETW·UWP 데이터)을 읽어야 해서 아직 구현하지 않았습니다.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = BodyFont,
        });

        _pages = new Control[] { _processes, _performance, _historyPage, _startup, _users, _details, _services };
        foreach (var page in _pages)
        {
            _content.Controls.Add(page);
        }

        _processes.MenuFactory = BuildRowMenu;
        _details.MenuFactory = BuildRowMenu;
        _services.MenuFactory = BuildRowMenu;
        _startup.MenuFactory = BuildRowMenu;
        _users.MenuFactory = BuildRowMenu;

        _processes.SelectionChangedByUser += UpdateToolbarState;
        _details.SelectionChangedByUser += UpdateToolbarState;
        _services.SelectionChangedByUser += UpdateToolbarState;
        _startup.SelectionChangedByUser += UpdateToolbarState;
        _users.SelectionChangedByUser += UpdateToolbarState;

        _pEnd = AddButton(_processes, "작업 끝내기", () => EndSelected(false));
        _pEco = AddButton(_processes, "효율성 모드", () => ToggleEcoSelected(_processes));
        AddButton(_processes, "새 작업 실행", NewTask);
        _dEnd = AddButton(_details, "작업 끝내기", () => EndSelected(false));
        _dEco = AddButton(_details, "효율성 모드", () => ToggleEcoSelected(_details));
        _sStart = AddButton(_services, "시작", () => ServiceAction(s => ServiceList.Start(s.Name)));
        _sStop = AddButton(_services, "중지", () => ServiceAction(s => ServiceList.Stop(s.Name)));
        _sRestart = AddButton(_services, "다시 시작", () => ServiceAction(s => ServiceList.Restart(s.Name)));
        AddButton(_services, "서비스 열기", () => Try(ServiceList.OpenServicesConsole));
        _stEnable = AddButton(_startup, "사용", () => StartupAction(st => StartupItems.SetEnabled(st, true)));
        _stDisable = AddButton(_startup, "사용 안 함", () => StartupAction(st => StartupItems.SetEnabled(st, false)));
        AddButton(_startup, "파일 위치 열기", () => StartupAction(st => ProcessActions.OpenFileLocation(StartupItems.ExtractPath(st.Command))));
        _uDisconnect = AddButton(_users, "연결 끊기", () => UserAction("이 사용자의 연결을 끊을까요?", id => Sessions.Disconnect(id)));
        _uLogoff = AddButton(_users, "로그오프", () => UserAction("이 사용자를 로그오프할까요? 저장되지 않은 작업은 잃을 수 있습니다.", id => Sessions.Logoff(id)));
        _uMessage = AddButton(_users, "메시지 보내기", SendUserMessage);

        BuildHeader();
        BuildStatusBar();
        BuildMenu();
        BuildTray();

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 190,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = NavNormal,
            Padding = new Padding(0, 8, 0, 0),
        };
        for (int i = 0; i < PageTitles.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Text = "   " + PageTitles[i],
                Width = 190,
                Height = 40,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = NavNormal,
                Margin = new Padding(0),
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => ShowPage(index);
            _nav[i] = button;
            nav.Controls.Add(button);
        }

        var topBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = TopBarColor };
        var appTitle = new Label { Text = "작업 관리자", AutoSize = true, Left = 16, Top = 13, Font = BodyFont };
        var searchLabel = new Label { Text = "검색", AutoSize = true, Top = 13, Font = BodyFont };
        topBar.Controls.Add(appTitle);
        topBar.Controls.Add(searchLabel);
        topBar.Controls.Add(_search);
        topBar.Resize += (_, _) =>
        {
            _search.Top = 9;
            _search.Left = (topBar.ClientSize.Width - _search.Width) / 2;
            searchLabel.Left = _search.Left - 40;
        };
        _search.TextChanged += (_, _) => ApplyFilter();

        var header = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(16, 8, 16, 8) };
        var headerRight = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        headerRight.Controls.Add(_groupCheck);
        headerRight.Controls.Add(_interval);
        headerRight.Controls.Add(new Label { Text = "갱신 주기", AutoSize = true, Padding = new Padding(0, 5, 8, 0) });
        header.Controls.Add(headerRight);
        header.Controls.Add(_pageTitle);

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = StatusBarColor, Padding = new Padding(8, 4, 8, 0) };
        var thresholdFlow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        thresholdFlow.Controls.Add(new Label { Text = "기록 임계치(%)", AutoSize = true, Padding = new Padding(0, 5, 4, 0) });
        thresholdFlow.Controls.Add(_threshold);
        thresholdFlow.Controls.Add(_logCheck);
        statusBar.Controls.Add(_status);
        statusBar.Controls.Add(thresholdFlow);

        // Dock은 나중에 추가한 컨트롤이 먼저 배치된다. 채울 영역(_content)을 먼저, 메뉴를 마지막에 넣는다.
        Controls.Add(_content);
        Controls.Add(nav);
        Controls.Add(statusBar);
        Controls.Add(header);
        Controls.Add(topBar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _timer.Tick += (_, _) => OnTick();
        Load += (_, _) =>
        {
            _perf.Start();
            OnTick();
            if (_settings.UpdateIntervalMs > 0)
            {
                _timer.Start();
            }
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _settings.HideWhenMinimized)
            {
                HideToTray();
            }
        };
        FormClosing += (_, _) =>
        {
            _settings.Save();
            _timer.Stop();
            _tray.Visible = false;
        };
        FormClosed += (_, _) =>
        {
            _perf.Dispose();
            _tray.Dispose();
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5)
            {
                _lastSlow = DateTime.MinValue;
                OnTick();
                e.Handled = true;
            }
        };

        ShowPage(PageProcesses);
        SetInterval(_settings.UpdateIntervalMs, save: false);
    }

    private IEnumerable<string> ColumnsFor(string key, IEnumerable<string> defaults)
    {
        return _settings.Columns.TryGetValue(key, out var saved) && saved.Count > 0 ? saved : defaults;
    }

    private static ToolStripButton AddButton(GridPage page, string text, Action action)
    {
        var button = new ToolStripButton(text);
        button.Click += (_, _) => action();
        page.Toolbar.Items.Add(button);
        return button;
    }

    private void BuildHeader()
    {
        _groupCheck.Checked = _settings.GroupProcesses;
        _groupCheck.CheckedChanged += (_, _) =>
        {
            _groupMenu.Checked = _groupCheck.Checked;
            _settings.GroupProcesses = _groupCheck.Checked;
            _settings.Save();
            RefreshCurrentPage();
        };

        _interval.Items.AddRange(IntervalNames);
        _interval.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _interval.SelectedIndex >= 0)
            {
                SetInterval(IntervalMs[_interval.SelectedIndex]);
            }
        };
    }

    private void BuildStatusBar()
    {
        _threshold.Value = Math.Clamp(_settings.ThresholdPercent, 1, 100);
        _threshold.ValueChanged += (_, _) =>
        {
            _settings.ThresholdPercent = (int)_threshold.Value;
            _settings.Save();
        };

        _logCheck.Checked = _settings.LogEnabled;
        _logCheck.CheckedChanged += (_, _) =>
        {
            _settings.LogEnabled = _logCheck.Checked;
            _settings.Save();
        };
    }

    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("파일");
        file.DropDownItems.Add(Item("새 작업 실행...", NewTask, Keys.Control | Keys.N));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("종료", Close));

        var options = new ToolStripMenuItem("옵션");
        _alwaysOnTop.Checked = _settings.AlwaysOnTop;
        _minimizeOnUse.Checked = _settings.MinimizeOnUse;
        _hideWhenMinimized.Checked = _settings.HideWhenMinimized;
        TopMost = _settings.AlwaysOnTop;
        _alwaysOnTop.Click += (_, _) =>
        {
            TopMost = _alwaysOnTop.Checked;
            _settings.AlwaysOnTop = _alwaysOnTop.Checked;
            _settings.Save();
        };
        _minimizeOnUse.Click += (_, _) =>
        {
            _settings.MinimizeOnUse = _minimizeOnUse.Checked;
            _settings.Save();
        };
        _hideWhenMinimized.Click += (_, _) =>
        {
            _settings.HideWhenMinimized = _hideWhenMinimized.Checked;
            _settings.Save();
        };
        options.DropDownItems.Add(_alwaysOnTop);
        options.DropDownItems.Add(_minimizeOnUse);
        options.DropDownItems.Add(_hideWhenMinimized);
        options.DropDownItems.Add(new ToolStripSeparator());

        var speed = new ToolStripMenuItem("업데이트 속도");
        for (int i = 0; i < IntervalMs.Length; i++)
        {
            int index = i;
            _intervalMenu[i] = Item(IntervalNames[i], () => SetInterval(IntervalMs[index]));
            _intervalMenu[i].CheckOnClick = false;
            speed.DropDownItems.Add(_intervalMenu[i]);
        }

        options.DropDownItems.Add(speed);

        var view = new ToolStripMenuItem("보기");
        view.DropDownItems.Add(Item("새로 고침", () =>
        {
            _lastSlow = DateTime.MinValue;
            OnTick();
        }, Keys.F5));
        view.DropDownItems.Add(Item("열 선택...", ChooseColumns));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Item("모두 펼치기", () => _processes.ExpandAll(true)));
        view.DropDownItems.Add(Item("모두 접기", () => _processes.ExpandAll(false)));
        view.DropDownItems.Add(new ToolStripSeparator());
        _groupMenu.Checked = _settings.GroupProcesses;
        _groupMenu.Click += (_, _) => _groupCheck.Checked = _groupMenu.Checked;
        view.DropDownItems.Add(_groupMenu);

        var help = new ToolStripMenuItem("도움말");
        help.DropDownItems.Add(Item("정보", () => MessageBox.Show(this,
            "작업 관리자 (TaskMonitor)" + Environment.NewLine
            + "Windows 시스템 정보 API(ntdll, PDH, WTS, SCM)로 만든 작업 관리자입니다.",
            "정보", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        _menu.Items.Add(file);
        _menu.Items.Add(options);
        _menu.Items.Add(view);
        _menu.Items.Add(help);
    }

    private void BuildTray()
    {
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "작업 관리자";
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("열기", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("종료", null, (_, _) => Close());
        _tray.ContextMenuStrip = trayMenu;
    }

    private void HideToTray()
    {
        Hide();
        _tray.Visible = true;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    private void ShowPage(int index)
    {
        _currentPage = index;
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visible = i == index;
        }

        for (int i = 0; i < _nav.Length; i++)
        {
            _nav[i].BackColor = i == index ? NavSelected : NavNormal;
        }

        _pageTitle.Text = PageTitles[index];
        _groupCheck.Visible = index == PageProcesses;
        _lastSlow = DateTime.MinValue;
        UpdateToolbarState();
        RefreshCurrentPage();
    }

    private void SetInterval(int ms, bool save = true)
    {
        _settings.UpdateIntervalMs = ms;
        _timer.Stop();
        if (ms > 0)
        {
            _timer.Interval = ms;
            if (IsHandleCreated)
            {
                _timer.Start();
            }
        }

        int index = Array.IndexOf(IntervalMs, ms);
        if (index < 0)
        {
            index = 1;
        }

        _syncing = true;
        _interval.SelectedIndex = index;
        for (int i = 0; i < _intervalMenu.Length; i++)
        {
            _intervalMenu[i].Checked = i == index;
        }

        _syncing = false;
        if (save)
        {
            _settings.Save();
        }
    }

    private void OnTick()
    {
        try
        {
            var snap = _sampler.Sample();
            var perf = _perf.Latest;
            _lastPerf = perf;
            if (snap is null)
            {
                _status.Text = "기준점 수집 중... 다음 갱신부터 표시됩니다.";
                return;
            }

            _last = snap;
            _info.BeginTick(40);
            _info.Prune(snap.Processes.Select(p => (p.Pid, p.CreateTime)));
            _performance.Update(snap, perf);

            if (_settings.LogEnabled && snap.TotalCpuPercent >= _settings.ThresholdPercent)
            {
                SpikeLog.Append(snap);
            }

            RefreshCurrentPage();
            if (DateTime.Now - _lastSlow >= SlowRefresh)
            {
                _lastSlow = DateTime.Now;
                RefreshSlowPages();
            }

            _status.Text = $"마지막 갱신 {DateTime.Now:HH:mm:ss}   |   프로세스 {snap.ProcessCount}개   |   기록 파일: {SpikeLog.FilePath}";
        }
        catch (Exception ex)
        {
            _status.Text = $"오류: {ex.Message}";
        }
    }

    // 자주 바뀌는 페이지(프로세스, 세부 정보)는 매 틱 다시 그린다.
    private void RefreshCurrentPage()
    {
        if (_last is null)
        {
            return;
        }

        if (_currentPage == PageProcesses)
        {
            _processes.SetRows(RowBuilders.Processes(_last, _info, _settings.GroupProcesses));
            UpdateBigHeaders();
        }
        else if (_currentPage == PageDetails)
        {
            _details.SetRows(RowBuilders.Details(_last, _info));
        }
    }

    // 서비스, 시작 프로그램, 사용자는 열거 비용이 있어서 3초마다 다시 읽는다.
    private void RefreshSlowPages()
    {
        switch (_currentPage)
        {
            case PageServices:
                _services.SetRows(RowBuilders.Services(ServiceList.Enumerate()));
                break;
            case PageStartup:
                RefreshStartup();
                break;
            case PageUsers:
                RefreshUsers();
                break;
        }
    }

    private void RefreshStartup() => _startup.SetRows(RowBuilders.Startup(StartupItems.Enumerate()));

    private void RefreshUsers() => _users.SetRows(RowBuilders.Users(Sessions.Enumerate()));

    private void UpdateBigHeaders()
    {
        if (_last is null)
        {
            return;
        }

        var cpuColor = _last.TotalCpuPercent >= _settings.ThresholdPercent ? Color.Firebrick : Color.Black;
        _processes.SetBig("cpu", $"{_last.TotalCpuPercent:F0}%", cpuColor);

        double memory = _lastPerf is { TotalMemoryBytes: > 0 } p
            ? 100.0 * (p.TotalMemoryBytes - p.AvailableMemoryBytes) / p.TotalMemoryBytes
            : 0;
        _processes.SetBig("memory", $"{memory:F0}%", Color.Black);
    }

    private void ApplyFilter()
    {
        foreach (var page in new[] { _processes, _details, _services, _startup, _users })
        {
            page.SetFilter(_search.Text);
        }
    }

    private void UpdateToolbarState()
    {
        bool hasProcess = SelectedTarget(_processes) is not null;
        _pEnd.Enabled = hasProcess;
        _pEco.Enabled = hasProcess;

        bool hasDetail = SelectedTarget(_details) is not null;
        _dEnd.Enabled = hasDetail;
        _dEco.Enabled = hasDetail;

        bool hasService = _services.SelectedRow?.Tag is ServiceRow;
        _sStart.Enabled = hasService;
        _sStop.Enabled = hasService;
        _sRestart.Enabled = hasService;

        bool hasStartup = _startup.SelectedRow?.Tag is StartupRow;
        _stEnable.Enabled = hasStartup;
        _stDisable.Enabled = hasStartup;

        bool hasUser = _users.SelectedRow?.Tag is SessionRow;
        _uDisconnect.Enabled = hasUser;
        _uLogoff.Enabled = hasUser;
        _uMessage.Enabled = hasUser;
    }

    private static ProcTarget? SelectedTarget(GridPage page) =>
        page.SelectedRow?.Tag is ProcTarget target && target.Members.Count > 0 ? target : null;

    private ContextMenuStrip? BuildRowMenu(Row row)
    {
        return row.Tag switch
        {
            ProcTarget target when target.Members.Count > 0 => BuildProcessMenu(target),
            ServiceRow service => BuildServiceMenu(service),
            StartupRow startup => BuildStartupMenu(startup),
            SessionRow session => BuildUserMenu(session),
            _ => null,
        };
    }

    private ContextMenuStrip BuildProcessMenu(ProcTarget target)
    {
        var menu = new ContextMenuStrip();
        var first = target.Members[0];

        menu.Items.Add(Item("창으로 전환", () => SwitchTo(first.Pid)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("작업 끝내기", () => EndTarget(target, false)));
        menu.Items.Add(Item("작업 트리 끝내기", () => EndTarget(target, true)));
        menu.Items.Add(new ToolStripSeparator());

        var eco = Item("효율성 모드", () => ToggleEco(target));
        eco.Checked = SafeGet(() => ProcessActions.IsEfficiencyMode(first.Pid), false);
        menu.Items.Add(eco);

        var priority = new ToolStripMenuItem("우선 순위 설정");
        var current = SafeGet<PriorityLevel?>(() => ProcessActions.GetPriority(first.Pid), null);
        foreach (var level in Enum.GetValues<PriorityLevel>())
        {
            var item = Item(ProcessActions.PriorityName(level), () => ForEachMember(target, m => ProcessActions.SetPriority(m.Pid, level)));
            item.Checked = current == level;
            priority.DropDownItems.Add(item);
        }

        menu.Items.Add(priority);
        menu.Items.Add(Item("선호도 설정...", () => ChangeAffinity(target)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("덤프 파일 만들기", () => DumpTarget(target)));
        menu.Items.Add(Item("파일 위치 열기", () => Try(() => ProcessActions.OpenFileLocation(ProcessInfoCache.PathOf(first.Pid)))));
        menu.Items.Add(Item("온라인 검색", () => Try(() => ProcessActions.SearchOnline(target.Name))));
        menu.Items.Add(Item("속성", () => Try(() => ProcessActions.ShowProperties(ProcessInfoCache.PathOf(first.Pid)))));

        if (!target.IsGroup)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("세부 정보로 이동", () => GoToDetails(first.Pid)));
        }

        return menu;
    }

    private ContextMenuStrip BuildServiceMenu(ServiceRow service)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Item("시작", () => ServiceAction(_ => ServiceList.Start(service.Name), service)));
        menu.Items.Add(Item("중지", () => ServiceAction(_ => ServiceList.Stop(service.Name), service)));
        menu.Items.Add(Item("다시 시작", () => ServiceAction(_ => ServiceList.Restart(service.Name), service)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("온라인 검색", () => Try(() => ProcessActions.SearchOnline(
            service.DisplayName.Length > 0 ? service.DisplayName : service.Name))));
        menu.Items.Add(Item("서비스 열기", () => Try(ServiceList.OpenServicesConsole)));
        if (service.ProcessId > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("세부 정보로 이동", () => GoToDetails(service.ProcessId)));
        }

        return menu;
    }

    private ContextMenuStrip BuildStartupMenu(StartupRow startup)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Item(startup.Enabled ? "사용 안 함" : "사용", () => StartupAction(st => StartupItems.SetEnabled(st, !startup.Enabled), startup)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("파일 위치 열기", () => Try(() => ProcessActions.OpenFileLocation(StartupItems.ExtractPath(startup.Command)))));
        menu.Items.Add(Item("속성", () => Try(() => ProcessActions.ShowProperties(StartupItems.ExtractPath(startup.Command)))));
        menu.Items.Add(Item("온라인 검색", () => Try(() => ProcessActions.SearchOnline(startup.Name))));
        return menu;
    }

    private ContextMenuStrip BuildUserMenu(SessionRow session)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Item("연결 끊기", () => UserAction("이 사용자의 연결을 끊을까요?", id => Sessions.Disconnect(id), session)));
        menu.Items.Add(Item("로그오프", () => UserAction("이 사용자를 로그오프할까요? 저장되지 않은 작업은 잃을 수 있습니다.", id => Sessions.Logoff(id), session)));
        menu.Items.Add(Item("메시지 보내기", () => SendUserMessage(session)));
        return menu;
    }

    private void EndSelected(bool tree)
    {
        var page = _currentPage == PageDetails ? _details : _processes;
        var target = SelectedTarget(page);
        if (target is not null)
        {
            EndTarget(target, tree);
        }
    }

    private void EndTarget(ProcTarget target, bool tree)
    {
        string what = target.IsGroup ? $"{target.Name} ({target.Members.Count}개)" : target.Name;
        string title = tree ? "작업 트리 끝내기" : "작업 끝내기";
        var answer = MessageBox.Show(this,
            $"{what} 작업을 끝내시겠습니까?{Environment.NewLine}저장되지 않은 데이터는 잃을 수 있습니다.",
            title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        if (!tree)
        {
            ForEachMember(target, m => ProcessActions.EndTask(m.Pid));
            return;
        }

        IReadOnlyList<ProcessUsage> all = _last?.Processes ?? Array.Empty<ProcessUsage>();
        int failed = 0;
        foreach (var member in target.Members)
        {
            failed += ProcessActions.EndTree(member.Pid, all);
        }

        if (failed > 0)
        {
            MessageBox.Show(this, $"{failed}개 프로세스는 끝내지 못했습니다(권한 부족 또는 이미 종료됨).", title,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleEcoSelected(GridPage page)
    {
        var target = SelectedTarget(page);
        if (target is not null)
        {
            ToggleEco(target);
        }
    }

    private void ToggleEco(ProcTarget target)
    {
        bool current = SafeGet(() => ProcessActions.IsEfficiencyMode(target.Members[0].Pid), false);
        ForEachMember(target, m => ProcessActions.SetEfficiencyMode(m.Pid, !current));
    }

    private void ChangeAffinity(ProcTarget target)
    {
        var first = target.Members[0];
        ulong current;
        try
        {
            current = ProcessActions.GetAffinity(first.Pid);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "선호도 설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ulong? mask = Dialogs.Affinity(this, Environment.ProcessorCount, current);
        if (mask is { } chosen)
        {
            ForEachMember(target, m => ProcessActions.SetAffinity(m.Pid, chosen));
        }
    }

    private void DumpTarget(ProcTarget target)
    {
        var first = target.Members[0];
        Try(() =>
        {
            string path = ProcessActions.WriteDump(first.Pid, first.Name);
            var answer = MessageBox.Show(this,
                $"덤프 파일을 저장했습니다.{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}폴더를 열까요?",
                "덤프 파일 만들기", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes)
            {
                ProcessActions.OpenFileLocation(path);
            }
        });
    }

    private void SwitchTo(int pid)
    {
        Try(() =>
        {
            if (!ProcessActions.SwitchTo(pid))
            {
                MessageBox.Show(this, "이 프로세스에서 보이는 창을 찾지 못했습니다.", "창으로 전환",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_settings.MinimizeOnUse)
            {
                WindowState = FormWindowState.Minimized;
            }
        });
    }

    private void GoToDetails(int pid)
    {
        ShowPage(PageDetails);
        _details.SelectKey("pid:" + pid);
    }

    private void NewTask()
    {
        var run = Dialogs.RunNewTask(this);
        if (run is { } task)
        {
            Try(() => ProcessActions.RunNewTask(task.Command, task.Admin));
        }
    }

    private void ServiceAction(Action<ServiceRow> action, ServiceRow? row = null)
    {
        var service = row ?? _services.SelectedRow?.Tag as ServiceRow;
        if (service is null)
        {
            return;
        }

        Try(() => action(service));
        RefreshSlowPagesNow();
    }

    private void StartupAction(Action<StartupRow> action, StartupRow? row = null)
    {
        var startup = row ?? _startup.SelectedRow?.Tag as StartupRow;
        if (startup is null)
        {
            return;
        }

        Try(() => action(startup));
        RefreshStartup();
    }

    private void UserAction(string question, Action<int> action, SessionRow? row = null)
    {
        var session = row ?? _users.SelectedRow?.Tag as SessionRow;
        if (session is null)
        {
            return;
        }

        if (MessageBox.Show(this, question, "사용자", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        Try(() => action(session.Id));
        RefreshUsers();
    }

    private void SendUserMessage()
    {
        if (_users.SelectedRow?.Tag is SessionRow session)
        {
            SendUserMessage(session);
        }
    }

    private void SendUserMessage(SessionRow session)
    {
        string? text = Dialogs.SendMessage(this);
        if (text is not null)
        {
            Try(() => Sessions.SendMessage(session.Id, "작업 관리자", text));
        }
    }

    private void RefreshSlowPagesNow()
    {
        _lastSlow = DateTime.MinValue;
        RefreshSlowPages();
    }

    private void ChooseColumns()
    {
        GridPage? page = _currentPage switch
        {
            PageProcesses => _processes,
            PageDetails => _details,
            _ => null,
        };
        if (page is null)
        {
            MessageBox.Show(this, "열 선택은 프로세스와 세부 정보 페이지에서 쓸 수 있습니다.", "열 선택",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var keys = Dialogs.ChooseColumns(this, page.AllColumns, page.VisibleColumns);
        if (keys is null || keys.Count == 0)
        {
            return;
        }

        page.SetColumns(keys);
        _settings.Columns[page == _processes ? "processes" : "details"] = keys.ToList();
        _settings.Save();
        RefreshCurrentPage();
    }

    // 작업 실행 중 예외는 메시지 상자로 보여주고 프로그램은 계속 돌린다.
    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "작업 관리자", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // 묶음 행이면 모든 구성원에게 적용하고, 실패한 항목만 모아서 한 번에 보여준다.
    private void ForEachMember(ProcTarget target, Action<ProcessUsage> action)
    {
        var errors = new List<string>();
        foreach (var member in target.Members)
        {
            try
            {
                action(member);
            }
            catch (Exception ex)
            {
                errors.Add($"{member.Name}({member.Pid}): {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors.Take(8)), "작업 실패",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static T SafeGet<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static ToolStripMenuItem Item(string text, Action action, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeys = shortcut };
        item.Click += (_, _) => action();
        return item;
    }
}
