using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TaskMonitor;

// 작업 관리자 메인 창: 메뉴, 왼쪽 탐색, 7개 페이지, 하단 상태 바.
// 데이터는 DataWorker(백그라운드)가 모으고, 이 창은 결과(Frame)를 받아 그리기만 한다.
// 오래 걸릴 수 있는 작업(서비스 제어, 덤프, 세션, 셸 열기)은 RunBackground로 UI 스레드 밖에서 실행한다.
internal sealed class MainForm : Form
{
    internal const int PageProcesses = 0;
    internal const int PagePerformance = 1;
    internal const int PageHistory = 2;
    internal const int PageStartup = 3;
    internal const int PageUsers = 4;
    internal const int PageDetails = 5;
    internal const int PageServices = 6;
    internal const int PageEvents = 7;
    private const int FreezeThresholdMs = 2000;
    private const int SlowUiMs = 150;

    private static readonly string[] PageTitles = { "프로세스", "성능", "앱 기록", "시작 프로그램", "사용자", "세부 정보", "서비스", "이벤트" };
    private static readonly int[] IntervalMs = { 500, 1000, 4000, 0 };
    private static readonly string[] IntervalNames = { "0.5 초", "1 초", "4 초", "일시 중지" };
    private static Color NavSelected => Theme.NavSel;
    private static Color NavNormal => Theme.Nav;
    private static Color TopBarColor => Theme.TopBar;
    private static Color StatusBarColor => Theme.Nav;
    private static readonly Font BodyFont = new("Segoe UI Variable Text", 9F);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SystemPerf _perf = new();
    // 폼 생성자에서는 WinForms 동기화 컨텍스트가 아직 없으므로 Load에서 잡는다.
    private SynchronizationContext? _ui;
    private readonly NotifyIcon _tray = new();
    private DataWorker? _worker;
    private volatile DataWorker.Frame? _pending;
    private int _posted;

    // 프리징 감시: UI 스레드가 응답하는지 확인하고, 멈추면 진행 중이던 작업을 기록한다.
    private volatile string _currentWork = "없음";
    private volatile string _uiStage = "대기";
    private volatile bool _closing;
    private long _lastUiBeat = Environment.TickCount64;
    private int _beatPending;

    private readonly GridPage _processes;
    private readonly GridPage _details;
    private readonly GridPage _services;
    private readonly GridPage _startup;
    private readonly GridPage _users;
    private readonly PerformancePage _performance = new() { Dock = DockStyle.Fill };
    private readonly GridPage _historyPage = new(RowBuilders.HistoryColumns, RowBuilders.HistoryColumns.Select(c => c.Key)) { Dock = DockStyle.Fill };
    private readonly GridPage _eventsPage = new(RowBuilders.ActivityColumns, RowBuilders.ActivityColumns.Select(c => c.Key)) { Dock = DockStyle.Fill };
    private readonly Control[] _pages;
    private readonly Button[] _nav = new Button[PageTitles.Length];
    private readonly IReadOnlyList<Row>?[] _appliedRows = new IReadOnlyList<Row>?[PageTitles.Length];
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _navPanel = new() { Dock = DockStyle.Left, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 200, Padding = new Padding(4, 6, 4, 0) };
    private readonly Panel _topBar = new();
    private readonly Panel _headerPanel = new();
    private readonly Panel _statusBar = new();
    private readonly Button _navToggle = new();
    private readonly FlowLayoutPanel _rateFlow = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    private readonly Button[] _rateButtons = new Button[IntervalMs.Length];
    private bool _navOpen = true;

    private readonly TextBox _search = new() { Width = 360 };
    private readonly CheckBox _groupCheck = new() { Text = "이름별 합산", AutoSize = true };
    private readonly NumericUpDown _threshold = new() { Minimum = 1, Maximum = 100, Width = 60 };
    private readonly CheckBox _logCheck = new() { Text = "임계치 이상이면 기록", AutoSize = true };
    private readonly Label _pageTitle = new() { AutoSize = true, Dock = DockStyle.Left, Font = new Font("Segoe UI Variable Display Semibold", 21F) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
    private readonly MenuStrip _menu = new();

    private readonly ToolStripMenuItem _alwaysOnTop = new("항상 위에 표시") { CheckOnClick = true };
    private readonly ToolStripMenuItem _minimizeOnUse = new("사용하지 않을 때 최소화") { CheckOnClick = true };
    private readonly ToolStripMenuItem _hideWhenMinimized = new("최소화 시 숨기기") { CheckOnClick = true };
    private readonly ToolStripMenuItem _groupMenu = new("이름별 합산") { CheckOnClick = true };
    private readonly ToolStripMenuItem _treeMenu = new("프로세스 트리로 보기") { CheckOnClick = true };
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
    private readonly ToolStripButton _alertsButton;
    private bool _alertsOnlyOn;

    // 실행 파일에 넣은 아이콘(csproj ApplicationIcon). 창, 작업 표시줄, 트레이가 같은 아이콘을 쓴다.
    private static readonly Icon AppIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _activateWait;
    // 메뉴의 "종료"처럼 정말 끝낼 때 켠다. 꺼져 있으면 X 단추는 설정에 따라 트레이로 숨긴다.
    private bool _exiting;
    private string? _lastNotifiedAlert;
    private ToolStripMenuItem? _updateItem;      // 새 버전이 있으면 메뉴 줄 오른쪽에 뜬다
    private bool _updateStaged;                  // 새 exe를 이미 놓았다. 다시 받지 않는다
    private bool _updateBalloon;                 // 마지막 트레이 알림이 업데이트 알림이다

    private int _currentPage = PageProcesses;
    private Snapshot? _last;
    private PerfSnapshot? _lastPerf;

    // activate: 두 번째 실행이 신호를 주는 이벤트(Program이 만든다). startInTray: Windows 시작 시 실행처럼 창 없이 트레이로 시작.
    public MainForm(EventWaitHandle activate, bool startInTray)
    {
        _activate = activate;
        if (startInTray)
        {
            // 창을 잠깐도 보이지 않게 최소화·작업 표시줄 숨김으로 띄웠다가 Shown에서 트레이로 보낸다(Load는 창을 띄워야 돈다).
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Shown += (_, _) =>
            {
                HideToTray();
                ShowInTaskbar = true;
            };
        }

        Text = "TaskMonitor";
        Icon = AppIcon;
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Theme.Apply(_settings.ColorTheme);
        Strings.Apply(_settings.Language);
        BackColor = Theme.Bg;

        _processes = new GridPage(RowBuilders.ProcessColumns, ColumnsFor("processes", RowBuilders.DefaultProcessColumns)) { Dock = DockStyle.Fill };
        _details = new GridPage(RowBuilders.DetailColumns, ColumnsFor("details", RowBuilders.DefaultDetailColumns)) { Dock = DockStyle.Fill };
        _services = new GridPage(RowBuilders.ServiceColumns, RowBuilders.ServiceColumns.Select(c => c.Key)) { Dock = DockStyle.Fill };
        _startup = new GridPage(RowBuilders.StartupColumns, RowBuilders.StartupColumns.Select(c => c.Key)) { Dock = DockStyle.Fill };
        _users = new GridPage(RowBuilders.UserColumns, RowBuilders.UserColumns.Select(c => c.Key)) { Dock = DockStyle.Fill };


        _pages = new Control[] { _processes, _performance, _historyPage, _startup, _users, _details, _services, _eventsPage };
        _eventsPage.EmptyText = Strings.Localize("아직 기록이 없습니다. 프로세스 시작·종료, 경고, 게임 세션이 여기에 쌓입니다.");
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
        _sStart = AddButton(_services, "시작", () => ServiceAction("서비스 시작", ServiceList.Start));
        _sStop = AddButton(_services, "중지", () => ServiceAction("서비스 중지", ServiceList.Stop, confirm: true));
        _sRestart = AddButton(_services, "다시 시작", () => ServiceAction("서비스 다시 시작", ServiceList.Restart, confirm: true));
        AddButton(_services, "서비스 열기", () => RunBackground("서비스 관리자 열기", () => Attempt(ServiceList.OpenServicesConsole)));
        _stEnable = AddButton(_startup, "사용", () => StartupAction(st => StartupItems.SetEnabled(st, true)));
        _stDisable = AddButton(_startup, "사용 안 함", () => StartupAction(st => StartupItems.SetEnabled(st, false)));
        AddButton(_startup, "파일 위치 열기", () => StartupAction(st => ProcessActions.OpenFileLocation(StartupItems.ExtractPath(st.Command))));
        _uDisconnect = AddButton(_users, "연결 끊기", () => UserAction("이 사용자의 연결을 끊을까요?", id => Sessions.Disconnect(id)));
        _uLogoff = AddButton(_users, "로그오프", () => UserAction("이 사용자를 로그오프할까요? 저장되지 않은 작업은 잃을 수 있습니다.", id => Sessions.Logoff(id)));
        _uMessage = AddButton(_users, "메시지 보내기", SendUserMessage);
        AddButton(_historyPage, "기록 지우기", ResetHistory);
        AddButton(_eventsPage, "기록 폴더 열기", OpenRecordsFolder);
        _alertsButton = AddButton(_eventsPage, "경고만 보기", ToggleAlertsOnly);

        BuildHeader();
        BuildStatusBar();
        BuildMenu();
        BuildTray();

        var nav = _navPanel;
        nav.BackColor = NavNormal;
        _navToggle.Text = "≡";
        _navToggle.Size = new Size(40, 36);
        _navToggle.FlatStyle = FlatStyle.Flat;
        _navToggle.FlatAppearance.BorderSize = 0;
        _navToggle.Margin = new Padding(0, 0, 0, 4);
        _navToggle.Click += (_, _) => SetNavOpen(!_navOpen);
        nav.Controls.Add(_navToggle);
        for (int i = 0; i < PageTitles.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Height = 36,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = NavNormal,
                Margin = new Padding(0, 0, 0, 2),
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => ShowPage(index);
            _nav[i] = button;
            nav.Controls.Add(button);
        }
        SetNavOpen(true);

        var topBar = _topBar;
        topBar.Dock = DockStyle.Top;
        topBar.Height = 44;
        topBar.BackColor = TopBarColor;
        var appTitle = new Label { Text = "TaskMonitor", AutoSize = true, Left = 16, Top = 13, Font = BodyFont };
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

        var header = _headerPanel;
        header.Dock = DockStyle.Top;
        header.Height = 52;
        header.Padding = new Padding(20, 8, 20, 8);
        header.BackColor = Theme.Bg;
        var headerRight = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        headerRight.Controls.Add(_groupCheck);
        headerRight.Controls.Add(_rateFlow);
        headerRight.Controls.Add(new Label { Text = "갱신 주기", AutoSize = true, Padding = new Padding(0, 5, 8, 0) });
        header.Controls.Add(headerRight);
        header.Controls.Add(_pageTitle);

        var statusBar = _statusBar;
        statusBar.Dock = DockStyle.Bottom;
        statusBar.Height = 34;
        statusBar.BackColor = StatusBarColor;
        statusBar.Padding = new Padding(8, 4, 8, 0);
        var thresholdFlow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        thresholdFlow.Controls.Add(new Label { Text = "기록 임계치(%)", AutoSize = true, Font = BodyFont, Padding = new Padding(0, 5, 4, 0) });
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
        ApplyTheme();

        Load += (_, _) =>
        {
            _ui = SynchronizationContext.Current;
            _perf.Start();
            DiskHealthMonitor.Start();
            GpuDetailMonitor.Start();
            _worker = new DataWorker(Publish, _perf, _currentPage, _settings.UpdateIntervalMs,
                _settings.GroupProcesses, _settings.LogEnabled, _settings.ThresholdPercent);
            _worker.SetTree(_settings.ProcessTree);
            _worker.Start();
            StartWatchdog();
            _ = UpdateLoopAsync();
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
            {
                try
                {
                    if (!_closing && IsHandleCreated)
                    {
                        BeginInvoke(RestoreFromTray);
                    }
                }
                catch (InvalidOperationException)
                {
                    // 확인과 BeginInvoke 사이에 창이 닫혔다. 스레드 풀 예외는 프로세스를 끝내므로 삼킨다.
                }
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _settings.HideWhenMinimized)
            {
                HideToTray();
            }

            UpdateBackground();
        };
        VisibleChanged += (_, _) => UpdateBackground();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && _settings.CloseToTray && !_exiting)
            {
                // 기록과 경고는 켜져 있어야 의미가 있으므로, 원하면 X 단추로 끄지 않고 트레이로 보낸다.
                e.Cancel = true;
                HideToTray();
                return;
            }

            _closing = true;
            _activateWait?.Unregister(null);
            _settings.Save();
            _worker?.Dispose();
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
                _worker?.RequestNow();
                e.Handled = true;
            }
        };

        _status.Text = Strings.Localize("데이터 수집 중...");
        ShowPage(PageProcesses);
        SetInterval(_settings.UpdateIntervalMs, save: false);
    }

    private IEnumerable<string> ColumnsFor(string key, IEnumerable<string> defaults)
    {
        return _settings.Columns.TryGetValue(key, out var saved) && saved.Count > 0 ? saved : defaults;
    }

    private static ToolStripButton AddButton(GridPage page, string text, Action action)
    {
        var button = new ToolStripButton(Strings.Localize(text));
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
            ApplyWorkerConfig();
        };

        for (int i = 0; i < IntervalMs.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Text = IntervalNames[i],
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0),
                Padding = new Padding(6, 1, 6, 1),
            };
            button.FlatAppearance.BorderSize = 1;
            button.Click += (_, _) => SetInterval(IntervalMs[index]);
            _rateButtons[i] = button;
            _rateFlow.Controls.Add(button);
        }
    }

    private void BuildStatusBar()
    {
        _threshold.Value = Math.Clamp(_settings.ThresholdPercent, 1, 100);
        _threshold.ValueChanged += (_, _) =>
        {
            _settings.ThresholdPercent = (int)_threshold.Value;
            _settings.Save();
            ApplyWorkerConfig();
        };

        _logCheck.Checked = _settings.LogEnabled;
        _logCheck.CheckedChanged += (_, _) =>
        {
            _settings.LogEnabled = _logCheck.Checked;
            _settings.Save();
            ApplyWorkerConfig();
        };
    }

    private void ApplyWorkerConfig()
    {
        _worker?.Configure(_settings.GroupProcesses, _settings.LogEnabled, _settings.ThresholdPercent);
    }

    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("파일");
        file.DropDownItems.Add(Item("새 작업 실행...", NewTask, Keys.Control | Keys.N));
        file.DropDownItems.Add(new ToolStripSeparator());
        bool elevated = IsElevated();
        var elevate = Item(elevated ? "관리자 권한으로 실행 중" : "관리자 권한으로 다시 실행", RestartAsAdministrator);
        elevate.Enabled = !elevated;
        file.DropDownItems.Add(elevate);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("종료", ExitApp));

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
        var closeToTray = Item("닫기 단추로 트레이에 숨기기", () => { });
        closeToTray.CheckOnClick = true;
        closeToTray.Checked = _settings.CloseToTray;
        closeToTray.Click += (_, _) =>
        {
            _settings.CloseToTray = closeToTray.Checked;
            _settings.Save();
        };
        options.DropDownItems.Add(closeToTray);
        var autoStart = Item("Windows 시작 시 실행", () => { });
        autoStart.CheckOnClick = true;
        autoStart.Checked = AutoStart.IsEnabled();
        autoStart.Click += (_, _) =>
        {
            try
            {
                AutoStart.Set(autoStart.Checked);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                MessageBox.Show(this, ex.Message, Strings.Localize("Windows 시작 시 실행"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            autoStart.Checked = AutoStart.IsEnabled();
        };
        options.DropDownItems.Add(autoStart);
        var autoUpdate = Item("자동 업데이트", () => { });
        autoUpdate.CheckOnClick = true;
        autoUpdate.Checked = _settings.AutoUpdate;
        autoUpdate.Click += (_, _) =>
        {
            _settings.AutoUpdate = autoUpdate.Checked;
            _settings.Save();
        };
        options.DropDownItems.Add(autoUpdate);
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

        var themeMenu = new ToolStripMenuItem("테마");
        foreach (var (mode, label) in new[] { ("light", "밝게"), ("dark", "어둡게"), ("system", "시스템 설정 따름") })
        {
            var item = Item(label, () => SetColorTheme(mode));
            item.Checked = _settings.ColorTheme == mode;
            themeMenu.DropDownItems.Add(item);
        }

        options.DropDownItems.Add(themeMenu);

        var languageMenu = new ToolStripMenuItem(Strings.Localize("언어"));
        foreach (var (code, label) in new[] { ("ko", "한국어"), ("en", "English") })
        {
            var item = Item(label, () => SetLanguage(code));
            item.Checked = _settings.Language == code;
            languageMenu.DropDownItems.Add(item);
        }

        options.DropDownItems.Add(languageMenu);

        var view = new ToolStripMenuItem("보기");
        view.DropDownItems.Add(Item("새로 고침", () => _worker?.RequestNow(), Keys.F5));
        view.DropDownItems.Add(Item("열 선택...", ChooseColumns));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Item("모두 펼치기", () => _processes.ExpandAll(true)));
        view.DropDownItems.Add(Item("모두 접기", () => _processes.ExpandAll(false)));
        view.DropDownItems.Add(new ToolStripSeparator());
        _groupMenu.Checked = _settings.GroupProcesses;
        _groupMenu.Click += (_, _) => _groupCheck.Checked = _groupMenu.Checked;
        view.DropDownItems.Add(_groupMenu);
        _treeMenu.Checked = _settings.ProcessTree;
        _treeMenu.Click += (_, _) => SetProcessTree(_treeMenu.Checked);
        view.DropDownItems.Add(_treeMenu);

        var records = new ToolStripMenuItem("기록");
        records.DropDownItems.Add(Item("원인 분석 리포트 만들기 (최근 1시간)", CreateCauseReport));
        records.DropDownItems.Add(Item("기록 내보내기 (CSV)...", ExportRecords));
        records.DropDownItems.Add(Item("기록 비교...", OpenComparison));
        records.DropDownItems.Add(new ToolStripSeparator());
        records.DropDownItems.Add(Item("기록 폴더 열기", OpenRecordsFolder));

        var help = new ToolStripMenuItem("도움말");
        help.DropDownItems.Add(Item("업데이트 확인", async () => ShowUpdate(await Updater.CheckAsync(), manual: true)));
        help.DropDownItems.Add(Item("정보", () => MessageBox.Show(this,
            $"TaskMonitor {Application.ProductVersion.Split('+')[0]}" + Environment.NewLine
            + Strings.Localize("CPU를 누가 쓰는지 찾는 Windows 작업 관리자") + Environment.NewLine + Environment.NewLine
            + Strings.Localize("기록 위치") + ": " + RecordFolder.Path + Environment.NewLine
            + Strings.Localize("기록은 이 PC에만 저장되고 밖으로 보내지 않습니다.") + Environment.NewLine
            + Strings.Localize("오픈 소스 고지") + ": THIRD-PARTY-NOTICES.txt",
            Strings.Localize("정보"), MessageBoxButtons.OK, MessageBoxIcon.Information)));

        _menu.Items.Add(file);
        _menu.Items.Add(options);
        _menu.Items.Add(view);
        _menu.Items.Add(records);
        _menu.Items.Add(help);
    }

    private void BuildTray()
    {
        _tray.Icon = AppIcon;
        _tray.Text = "TaskMonitor";
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        // 경고 알림을 누르면 이벤트 페이지를 연다.
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_updateBalloon)
            {
                RestartForUpdate();
                return;
            }

            RestoreFromTray();
            ShowPage(PageEvents);
        };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(Strings.Localize("열기"), null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add(Strings.Localize("종료"), null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = trayMenu;
    }

    // 창이 안 보이면(최소화·트레이) 화면용 측정을 줄인다. 다시 보이면 지금 페이지의 표를 바로 다시 만든다.
    private void UpdateBackground()
    {
        bool background = !Visible || WindowState == FormWindowState.Minimized;
        bool returning = AppState.Background && !background;
        AppState.Background = background;
        if (returning)
        {
            _worker?.RequestPage(_currentPage);
        }
    }

    // 시작할 때와 하루 한 번 새 버전을 확인한다. 끄면 확인만 건너뛰고, 도움말 > 업데이트 확인은 그대로 된다.
    private async Task UpdateLoopAsync()
    {
        while (!_closing)
        {
            Updater.CleanupOld();
            if (_settings.AutoUpdate && !_updateStaged)
            {
                ShowUpdate(await Updater.CheckAsync());
            }

            await Task.Delay(TimeSpan.FromDays(1));
        }
    }

    private void ShowUpdate(Updater.Result result, bool manual = false)
    {
        if (_closing || _updateStaged)
        {
            return;
        }

        switch (result.Outcome)
        {
            case Updater.Outcome.Staged:
                _updateStaged = true;
                string ready = Strings.Fmt("새 버전 {0} 준비됨 — 지금 다시 시작", result.Latest);
                SetUpdateItem(ready, RestartForUpdate);
                if (manual)
                {
                    if (MessageBox.Show(this, ready + "?", "TaskMonitor", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        RestartForUpdate();
                    }
                }
                else if (!Visible && _tray.Visible)
                {
                    _updateBalloon = true;
                    _tray.ShowBalloonTip(10000, "TaskMonitor", ready, ToolTipIcon.Info);
                }

                break;
            case Updater.Outcome.Manual:
                SetUpdateItem(Strings.Fmt("새 버전 {0} 받기", result.Latest), OpenReleasesPage);
                if (manual)
                {
                    MessageBox.Show(this, Strings.Fmt("새 버전 {0}이 있습니다. 이 폴더에는 자동으로 바꿔 넣을 수 없어 Release 페이지를 엽니다.", result.Latest),
                        "TaskMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    OpenReleasesPage();
                }

                break;
            case Updater.Outcome.UpToDate when manual:
                MessageBox.Show(this, Strings.Fmt("최신 버전입니다 ({0}).", Updater.Current), "TaskMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case Updater.Outcome.Failed when manual:
                MessageBox.Show(this, Strings.Fmt("업데이트를 확인하지 못했습니다: {0}", result.Detail), "TaskMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }

    private void SetUpdateItem(string text, Action action)
    {
        if (_updateItem is not null)
        {
            _menu.Items.Remove(_updateItem);
        }

        _updateItem = new ToolStripMenuItem(text) { Alignment = ToolStripItemAlignment.Right };
        _updateItem.Font = new Font(_updateItem.Font, FontStyle.Bold);
        _updateItem.Click += (_, _) => action();
        _menu.Items.Add(_updateItem);
    }

    private static void OpenReleasesPage() =>
        Process.Start(new ProcessStartInfo(Updater.ReleasesPage) { UseShellExecute = true });

    // 새 exe는 이미 제자리에 있다. 같은 경로로 새로 띄우고 이 창은 끝낸다(새 프로세스는 --restarted로 단일 실행 잠금을 기다린다).
    private void RestartForUpdate()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Arguments = "--restarted" });
        ExitApp();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
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

    // 탐색 막대를 펼치거나 접는다. 접으면 메뉴 글자 한 자만 보인다.
    private void SetNavOpen(bool open)
    {
        _navOpen = open;
        _navPanel.Width = open ? 200 : 50;
        for (int i = 0; i < _nav.Length; i++)
        {
            _nav[i].Width = open ? 192 : 42;
            string title = Strings.Localize(PageTitles[i]);
            _nav[i].Text = open ? "   " + title : title[..1];
        }
    }

    // 갱신 주기 선택 버튼: 고른 칸만 강조한다.
    private void UpdateRateButtons(int index)
    {
        for (int i = 0; i < _rateButtons.Length; i++)
        {
            bool selected = i == index;
            _rateButtons[i].BackColor = selected ? Theme.Accent : Theme.Bg;
            _rateButtons[i].ForeColor = selected ? Theme.Bg : Theme.Fg;
            _rateButtons[i].FlatAppearance.BorderColor = Theme.Border;
        }
    }

    // 테마를 바꾼 뒤 이미 만든 화면 요소의 색을 다시 맞춘다.
    private void ApplyTheme()
    {
        BackColor = Theme.Bg;
        Theme.ApplyTextColors(this);
        TranslateTree(this);
        _topBar.BackColor = TopBarColor;
        _headerPanel.BackColor = Theme.Bg;
        _statusBar.BackColor = StatusBarColor;
        _navPanel.BackColor = NavNormal;
        _content.BackColor = Theme.Bg;
        _menu.BackColor = Theme.TopBar;
        _menu.ForeColor = Theme.Fg;
        _search.BackColor = Theme.Input;
        _search.ForeColor = Theme.Fg;
        _pageTitle.ForeColor = Theme.Fg;
        _status.ForeColor = Theme.Fg2;
        _navToggle.ForeColor = Theme.Fg;
        _navToggle.BackColor = NavNormal;
        foreach (var page in _pages)
        {
            page.BackColor = Theme.Bg;
            if (page is GridPage grid)
            {
                grid.Toolbar.BackColor = Theme.Bg;
                foreach (ToolStripItem item in grid.Toolbar.Items)
                {
                    item.ForeColor = Theme.Fg;
                }
            }
        }

        for (int i = 0; i < _nav.Length; i++)
        {
            _nav[i].BackColor = i == _currentPage ? NavSelected : NavNormal;
            _nav[i].ForeColor = Theme.Fg;
        }

        int rate = Array.IndexOf(IntervalMs, _settings.UpdateIntervalMs);
        UpdateRateButtons(rate >= 0 ? rate : 1);
        Invalidate(true);
    }

    // 현재 프로세스가 관리자 권한으로 실행 중인지 확인한다(UAC로 올라간 토큰이면 true).
    internal static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // UAC 확인 창을 띄워 이 앱을 관리자 권한으로 다시 실행한다. 승인하면 현재 창을 닫는다.
    // 사용자가 UAC 창에서 취소하면 아무 일도 하지 않는다.
    private void RestartAsAdministrator()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "--restarted",
        };
        try
        {
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Strings.Localize("관리자 권한으로 다시 실행"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ExitApp();
    }

    // 언어는 컨트롤을 만들 때 문구를 고르므로 다음 실행부터 적용된다.
    private void SetLanguage(string code)
    {
        _settings.Language = code;
        _settings.Save();
        MessageBox.Show(this, Strings.Localize("언어는 다시 시작하면 적용됩니다."), "TaskMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ResetHistory()
    {
        var answer = MessageBox.Show(this, Strings.Localize("앱 기록을 모두 지울까요?"), Strings.Localize("기록 지우기"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
        {
            _worker?.ResetHistory();
        }
    }

    // 이미 만든 화면 요소(폼, 라벨, 단추, 체크 상자, 메뉴, 도구 모음)의 문구를 현재 언어로 바꾼다.
    private static void TranslateTree(Control root)
    {
        if (root is Form or Label or ButtonBase or CheckBox or GroupBox)
        {
            root.Text = Strings.Localize(root.Text);
        }

        if (root is ToolStrip strip)
        {
            TranslateItems(strip.Items);
        }

        foreach (Control child in root.Controls)
        {
            TranslateTree(child);
        }
    }

    private static void TranslateItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (!string.IsNullOrEmpty(item.Text))
            {
                item.Text = Strings.Localize(item.Text);
            }

            if (item is ToolStripMenuItem menu)
            {
                TranslateItems(menu.DropDownItems);
            }
        }
    }

    private void SetColorTheme(string mode)
    {
        _settings.ColorTheme = mode;
        _settings.Save();
        Theme.Apply(mode);
        ApplyTheme();
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

        _pageTitle.Text = Strings.Localize(PageTitles[index]);
        _groupCheck.Visible = index == PageProcesses;
        UpdateToolbarState();
        _worker?.RequestPage(index);
    }

    private void SetInterval(int ms, bool save = true)
    {
        _settings.UpdateIntervalMs = ms;
        _worker?.SetInterval(ms);

        int index = Array.IndexOf(IntervalMs, ms);
        if (index < 0)
        {
            index = 1;
        }

        UpdateRateButtons(index);
        for (int i = 0; i < _intervalMenu.Length; i++)
        {
            _intervalMenu[i].Checked = i == index;
        }

        if (save)
        {
            _settings.Save();
        }
    }

    // 백그라운드 스레드가 호출한다. 결과가 여러 개 쌓였으면 가장 최근 것만 그린다.
    private void Publish(DataWorker.Frame frame)
    {
        _pending = frame;
        if (Interlocked.Exchange(ref _posted, 1) == 0)
        {
            _ui?.Post(_ =>
            {
                Interlocked.Exchange(ref _posted, 0);
                if (_pending is { } latest)
                {
                    ApplyFrame(latest);
                }
            }, null);
        }
    }

    // UI 스레드: 결과를 화면에 반영한다. 여기서는 무거운 계산을 하지 않는다.
    private void ApplyFrame(DataWorker.Frame frame)
    {
        if (IsDisposed)
        {
            return;
        }

        if (frame.Snapshot is { } snap)
        {
            _last = snap;
            _lastPerf = frame.Perf;
            UiStep("화면 반영: 성능", () => _performance.Update(snap, frame.Perf));
            string status = Strings.Fmt("마지막 갱신 {0}   |   프로세스 {1}개", DateTime.Now.ToString("HH:mm:ss"), snap.ProcessCount);
            // 네트워크·FPS 열이 "-"인 이유를 알려 준다. 세션을 못 열었으면 대부분 관리자 권한으로 다시 실행하면 풀린다(파일 메뉴).
            if (EtwLatest.State == EtwState.OpenFailed)
            {
                status += $"   |   {Strings.Localize("네트워크·FPS 측정 꺼짐(관리자 권한으로 다시 실행)")}";
            }
            else if (EtwLatest.State == EtwState.Stopped)
            {
                status += $"   |   {Strings.Localize("네트워크·FPS 측정 중단됨(다시 시작하면 복구)")}";
            }
            if (_worker?.CpuTemperatureC is { } cpuTemp)
            {
                status += $"   |   CPU {cpuTemp:F0}°C";
                if (_worker?.FanRpm is { Count: > 0 } fans)
                {
                    status += Strings.Fmt("   팬 {0} RPM", string.Join(" / ", fans.Select(rpm => rpm.ToString("F0"))));
                }
            }

            if (_worker?.LastAlert is { } alert)
            {
                // 상태 바는 한 줄이라 긴 경고는 자른다(전체 내용은 이벤트 페이지에 있다).
                string shown = alert.Length > 60 ? alert[..60] + "…" : alert;
                status += $"   |   {Strings.Localize("경고")}: {shown}";
                // 트레이에 숨어 있을 때는 아무도 상태 바를 보지 않으므로 새 경고를 알림으로 띄운다(같은 경고는 한 번만).
                if (alert != _lastNotifiedAlert && AppState.Background && _tray.Visible)
                {
                    _updateBalloon = false;
                    _tray.ShowBalloonTip(10000, "TaskMonitor", alert, ToolTipIcon.Warning);
                }

                _lastNotifiedAlert = alert;
            }

            _status.Text = status;
            if (_currentPage == PageProcesses)
            {
                UpdateBigHeaders();
            }
        }

        if (frame.Page == _currentPage && frame.Rows is { } rows && !ReferenceEquals(rows, _appliedRows[frame.Page]))
        {
            _appliedRows[frame.Page] = rows;
            UiStep("화면 반영: 표", () => GridFor(frame.Page)?.SetRows(rows));
        }

        // 오류가 있어도 받은 값은 위에서 반영했다. 상태 바에만 오류를 보인다.
        if (frame.Error is not null)
        {
            _status.Text = $"{Strings.Localize("오류")}: {frame.Error}";
        }
    }

    // UI 스레드의 한 단계. 감시 스레드가 멈춘 단계를 알 수 있게 이름을 남기고, 오래 걸리면 기록한다.
    private void UiStep(string name, Action work)
    {
        _uiStage = name;
        var clock = Stopwatch.StartNew();
        work();
        if (clock.ElapsedMilliseconds >= SlowUiMs)
        {
            FreezeLog.Write($"UI 단계 '{name}' {clock.ElapsedMilliseconds}ms");
        }

        _uiStage = "대기";
    }

    private GridPage? GridFor(int page) => page switch
    {
        PageProcesses => _processes,
        PageDetails => _details,
        PageHistory => _historyPage,
        PageServices => _services,
        PageEvents => _eventsPage,
        PageStartup => _startup,
        PageUsers => _users,
        _ => null,
    };

    private void UpdateBigHeaders()
    {
        if (_last is null)
        {
            return;
        }

        var cpuColor = _last.TotalCpuPercent >= _settings.ThresholdPercent ? Theme.Red : Theme.Fg;
        _processes.SetBig("cpu", $"{_last.TotalCpuPercent:F0}%", cpuColor);

        double memory = _lastPerf is { Memory.Total: > 0 } p ? 100.0 * p.Memory.Used / p.Memory.Total : 0;
        _processes.SetBig("memory", $"{memory:F0}%", Theme.Fg);

        // 작업 관리자처럼 가장 바쁜 디스크·GPU의 사용률을 보인다.
        double disk = _lastPerf?.Disks.Select(d => d.BusyPercent).DefaultIfEmpty(0).Max() ?? 0;
        _processes.SetBig("disk", $"{disk:F0}%", Theme.Fg);
        double gpu = _lastPerf?.Gpus.Select(g => g.Utilization).DefaultIfEmpty(0).Max() ?? 0;
        _processes.SetBig("gpu", $"{gpu:F0}%", Theme.Fg);
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
            var item = Item(ProcessActions.PriorityName(level), () =>
                RunBackground("우선 순위 변경", () => CollectErrors(target, m => ProcessActions.SetPriority(m.Key, level))));
            item.Checked = current == level;
            priority.DropDownItems.Add(item);
        }

        menu.Items.Add(priority);
        menu.Items.Add(Item("선호도 설정...", () => ChangeAffinity(target)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("덤프 파일 만들기", () => DumpTarget(target)));
        menu.Items.Add(Item("파일 위치 열기", () => RunBackground("파일 위치 열기", () => Attempt(() => ProcessActions.OpenFileLocation(ProcessInfoCache.PathOf(first.Pid))))));
        menu.Items.Add(Item("온라인 검색", () => RunBackground("온라인 검색", () => Attempt(() => ProcessActions.SearchOnline(target.Name)))));
        menu.Items.Add(Item("속성", () => RunBackground("속성 열기", () => Attempt(() => ProcessActions.ShowProperties(ProcessInfoCache.PathOf(first.Pid))))));
        menu.Items.Add(Item("분석 창 열기", () => OpenAnalysis(first.Pid)));

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
        menu.Items.Add(Item("시작", () => ServiceAction("서비스 시작", ServiceList.Start, service)));
        menu.Items.Add(Item("중지", () => ServiceAction("서비스 중지", ServiceList.Stop, service, confirm: true)));
        menu.Items.Add(Item("다시 시작", () => ServiceAction("서비스 다시 시작", ServiceList.Restart, service, confirm: true)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("온라인 검색", () => RunBackground("온라인 검색", () => Attempt(() => ProcessActions.SearchOnline(
            service.DisplayName.Length > 0 ? service.DisplayName : service.Name)))));
        menu.Items.Add(Item("서비스 열기", () => RunBackground("서비스 관리자 열기", () => Attempt(ServiceList.OpenServicesConsole))));
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
        menu.Items.Add(Item("파일 위치 열기", () => RunBackground("파일 위치 열기", () => Attempt(() => ProcessActions.OpenFileLocation(StartupItems.ExtractPath(startup.Command))))));
        menu.Items.Add(Item("속성", () => RunBackground("속성 열기", () => Attempt(() => ProcessActions.ShowProperties(StartupItems.ExtractPath(startup.Command))))));
        menu.Items.Add(Item("온라인 검색", () => RunBackground("온라인 검색", () => Attempt(() => ProcessActions.SearchOnline(startup.Name)))));
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
        string what = target.IsGroup ? Strings.Fmt("{0} ({1}개)", target.Name, target.Members.Count) : target.Name;
        string title = Strings.Localize(tree ? "작업 트리 끝내기" : "작업 끝내기");
        var answer = MessageBox.Show(this,
            Strings.Fmt("{0} 작업을 끝내시겠습니까?", what) + Environment.NewLine + Strings.Localize("저장되지 않은 데이터는 잃을 수 있습니다."),
            title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        if (!tree)
        {
            RunBackground(title, () => CollectErrors(target, m => ProcessActions.EndTask(m.Key)));
            return;
        }

        // 트리 끝내기는 자식까지 찾아야 하므로, 지금 보고 있는 목록을 복사해서 넘긴다.
        IReadOnlyList<ProcessUsage> all = (_last?.Processes ?? Array.Empty<ProcessUsage>()).ToList();
        RunBackground(title, () =>
        {
            int failed = 0;
            foreach (var member in target.Members)
            {
                failed += ProcessActions.EndTree(member, all);
            }

            return failed > 0 ? Strings.Fmt("{0}개 프로세스는 끝내지 못했습니다(권한 부족 또는 이미 종료됨).", failed) : null;
        });
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
        RunBackground("효율성 모드 변경", () =>
        {
            bool current = SafeGet(() => ProcessActions.IsEfficiencyMode(target.Members[0].Pid), false);
            return CollectErrors(target, m => ProcessActions.SetEfficiencyMode(m.Key, !current));
        });
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
            MessageBox.Show(this, ex.Message, Strings.Localize("선호도 설정"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ulong? mask = Dialogs.Affinity(this, Environment.ProcessorCount, current);
        if (mask is { } chosen)
        {
            RunBackground("선호도 변경", () => CollectErrors(target, m => ProcessActions.SetAffinity(m.Key, chosen)));
        }
    }

    // 덤프는 큰 프로세스에서 수 초~수십 초 걸린다. 백그라운드에서 쓰고, 끝나면 폴더를 열지 묻는다.
    private void DumpTarget(ProcTarget target)
    {
        var first = target.Members[0];
        string? path = null;
        RunBackground("덤프 파일 만들기", () =>
        {
            path = ProcessActions.WriteDump(first.Key, first.Name);
            return null;
        }, () =>
        {
            if (path is null)
            {
                return;
            }

            var answer = MessageBox.Show(this,
                Strings.Localize("덤프 파일을 저장했습니다.") + Environment.NewLine + path + Environment.NewLine + Environment.NewLine + Strings.Localize("폴더를 열까요?"),
                Strings.Localize("덤프 파일 만들기"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes)
            {
                RunBackground("파일 위치 열기", () => Attempt(() => ProcessActions.OpenFileLocation(path)));
            }
        });
    }

    // ShowWindow는 응답 없는 창을 가진 프로세스에서 호출한 쪽을 막을 수 있어서 백그라운드에서 한다.
    private void SwitchTo(int pid)
    {
        RunBackground("창으로 전환", () => Attempt(() =>
        {
            if (!ProcessActions.SwitchTo(pid))
            {
                throw new InvalidOperationException(Strings.Localize("이 프로세스에서 보이는 창을 찾지 못했습니다."));
            }
        }), () =>
        {
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
            RunBackground("새 작업 실행", () => Attempt(() => ProcessActions.RunNewTask(task.Command, task.Admin)));
        }
    }

    // 프로세스 목록 트리 보기: 설정에 남기고 다음 샘플링부터 트리로 그린다.
    private void SetProcessTree(bool tree)
    {
        _settings.ProcessTree = tree;
        _settings.Save();
        _treeMenu.Checked = tree;
        _worker?.SetTree(tree);
    }

    // 프로세스 분석 창: 한 프로세스의 실행 정보와 자원, 연결, 최근 활동을 1초마다 새로 보여 준다.
    private void OpenAnalysis(int pid)
    {
        var form = new ProcessAnalysisForm(Strings.Fmt("프로세스 분석 - PID {0}", pid),
            () => ProcessAnalysisText.Build(pid, _last, _lastPerf, _worker?.Activity));
        form.Show(this);
    }

    // 최근 1시간 원인 분석 리포트를 기록 폴더에 Markdown으로 쓰고 메모장으로 연다.
    private void CreateCauseReport()
    {
        string? path = null;
        RunBackground("원인 분석 리포트", () =>
        {
            if (_worker is not { } worker)
            {
                return Strings.Localize("아직 기록을 모으는 중입니다. 잠시 뒤에 다시 시도하세요.");
            }

            string folder = Path.Combine(RecordFolder.Path, "reports");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, $"cause-report-{DateTime.Now:yyyyMMdd-HHmmss}.md");
            string text = CauseReport.Build(DateTime.Now, worker.Resources, worker.Activity, worker.AppRows);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
            return null;
        }, () =>
        {
            if (path is not null)
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        });
    }

    // 기록 전체(자원, 활동)를 날짜가 붙은 폴더에 CSV로 내보낸다.
    private void ExportRecords()
    {
        string? folder = null;
        RunBackground("기록 내보내기", () =>
        {
            if (_worker is not { } worker)
            {
                return Strings.Localize("아직 기록을 모으는 중입니다. 잠시 뒤에 다시 시도하세요.");
            }

            folder = Path.Combine(RecordFolder.Path, "export", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            CauseReport.ExportCsv(worker.Resources, worker.Activity, folder);
            return null;
        }, () =>
        {
            if (folder is not null)
            {
                ProcessActions.OpenFileLocation(Path.Combine(folder, "resource-log.csv"));
            }
        });
    }

    // 이벤트 페이지: 경고만 보기 전환. 버튼 글자가 현재 상태를 보여 준다.
    private void ToggleAlertsOnly()
    {
        _alertsOnlyOn = !_alertsOnlyOn;
        _alertsButton.Checked = _alertsOnlyOn;
        _alertsButton.Text = Strings.Localize(_alertsOnlyOn ? "모두 보기" : "경고만 보기");
        _worker?.SetAlertsOnly(_alertsOnlyOn);
    }

    private void OpenComparison()
    {
        if (_worker is not { } worker)
        {
            MessageBox.Show(this, Strings.Localize("아직 기록을 모으는 중입니다. 잠시 뒤에 다시 시도하세요."), Strings.Localize("기록 비교"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        new RecordComparisonForm(worker.Resources, worker.Activity).Show(this);
    }

    private void OpenRecordsFolder()
    {
        RunBackground("기록 폴더 열기", () => Attempt(() =>
        {
            Directory.CreateDirectory(RecordFolder.Path);
            ProcessActions.OpenFileLocation(Path.Combine(RecordFolder.Path, "activity-log.jsonl"));
        }));
    }

    // confirm: 중지와 다시 시작은 그 서비스를 쓰는 프로그램을 멈추게 하므로 먼저 묻는다.
    private void ServiceAction(string title, Action<string> action, ServiceRow? row = null, bool confirm = false)
    {
        var service = row ?? _services.SelectedRow?.Tag as ServiceRow;
        if (service is null)
        {
            return;
        }

        if (confirm)
        {
            var answer = MessageBox.Show(this,
                Strings.Fmt("{0} ({1})에 대해 \"{2}\" 작업을 진행할까요?", service.DisplayName, service.Name, Strings.Localize(title)) + Environment.NewLine +
                Strings.Localize("이 서비스를 쓰는 프로그램은 잠깐 동작을 멈출 수 있습니다."),
                Strings.Localize(title), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        // 서비스 시작과 중지는 몇 초에서 최대 15초까지 걸릴 수 있다. UI 스레드에서 기다리면 화면이 멈춘다.
        RunBackground(title, () => Attempt(() => action(service.Name)), () => { });
    }

    private void StartupAction(Action<StartupRow> action, StartupRow? row = null)
    {
        var startup = row ?? _startup.SelectedRow?.Tag as StartupRow;
        if (startup is null)
        {
            return;
        }

        RunBackground("시작 프로그램 변경", () => Attempt(() => action(startup)));
    }

    private void UserAction(string question, Action<int> action, SessionRow? row = null)
    {
        var session = row ?? _users.SelectedRow?.Tag as SessionRow;
        if (session is null)
        {
            return;
        }

        if (MessageBox.Show(this, Strings.Localize(question), Strings.Localize("사용자"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        RunBackground("사용자 작업", () => Attempt(() => action(session.Id)));
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
            RunBackground("메시지 보내기", () => Attempt(() => Sessions.SendMessage(session.Id, Strings.Localize("작업 관리자"), text)));
        }
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
            MessageBox.Show(this, Strings.Localize("열 선택은 프로세스와 세부 정보 페이지에서 쓸 수 있습니다."), Strings.Localize("열 선택"),
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
    }

    // 오래 걸릴 수 있는 작업을 UI 스레드 밖에서 실행한다.
    // work는 실패 메시지(없으면 null)를 돌려준다. 완료 후 메시지 표시와 목록 갱신만 UI 스레드에서 한다.
    private void RunBackground(string title, Func<string?> work, Action? onSuccess = null)
    {
        _currentWork = title;
        _status.Text = Strings.Fmt("{0} 중...", Strings.Localize(title));
        Task.Run(() =>
        {
            string? error;
            try
            {
                error = work();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _ui?.Post(_ =>
            {
                if (IsDisposed)
                {
                    return;
                }

                _currentWork = "없음";
                if (error is not null)
                {
                    MessageBox.Show(this, error, Strings.Localize(title), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    onSuccess?.Invoke();
                }

                _worker?.RequestNow();
            }, null);
        });
    }

    // 백그라운드용: 예외를 메시지로 바꿔 돌려준다. RunBackground가 그 메시지를 화면에 보여준다.
    private static string? Attempt(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // 작업 실행 중 예외는 메시지 상자로 보여주고 프로그램은 계속 돌린다(UI 스레드에서만 쓴다).
    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Strings.Localize("작업 관리자"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // 백그라운드용: 묶음의 모든 구성원에게 적용하고, 실패한 항목을 모아 한 번에 돌려준다.
    private static string? CollectErrors(ProcTarget target, Action<ProcessUsage> action)
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

        return errors.Count > 0 ? string.Join(Environment.NewLine, errors.Take(8)) : null;
    }

    // ---- 프리징 감시 ----
    // UI 스레드가 2초 넘게 메시지를 처리하지 못하거나, 작업 스레드가 한 단계에서 2초 넘게 머무르면 freeze.log에 기록한다.
    // 기록에는 진행 중이던 작업 이름과 멈춘 단계 이름이 들어간다.
    private void StartWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            bool uiReported = false;
            bool workerReported = false;
            long uiFreezeStart = 0;
            while (!_closing)
            {
                Thread.Sleep(500);

                // UI 스레드가 처리할 수 있는 메시지를 하나 보내고, 그것이 처리되었는지로 응답 여부를 본다.
                if (Interlocked.CompareExchange(ref _beatPending, 1, 0) == 0)
                {
                    _ui?.Post(_ =>
                    {
                        _lastUiBeat = Environment.TickCount64;
                        Interlocked.Exchange(ref _beatPending, 0);
                    }, null);
                }

                long gap = Environment.TickCount64 - _lastUiBeat;
                if (gap > FreezeThresholdMs && !uiReported)
                {
                    uiReported = true;
                    uiFreezeStart = _lastUiBeat;
                    FreezeLog.Write($"UI 응답 없음 {gap}ms 이상, 진행 중이던 작업: {_currentWork}, UI 단계: {_uiStage}");
                }
                else if (gap < 500 && uiReported)
                {
                    uiReported = false;
                    FreezeLog.Write($"UI 응답 회복 (멈춤 약 {Environment.TickCount64 - uiFreezeStart}ms)");
                }

                var worker = _worker;
                if (worker is not null)
                {
                    string stage = worker.CurrentStage;
                    long age = worker.StageAgeMs;
                    if (stage != "대기" && age > FreezeThresholdMs && !workerReported)
                    {
                        workerReported = true;
                        FreezeLog.Write($"작업 스레드 멈춤 {age}ms 이상, 멈춘 단계: {stage}");
                    }
                    else if (stage == "대기" && workerReported)
                    {
                        workerReported = false;
                        FreezeLog.Write("작업 스레드 회복");
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "TaskMonitor watchdog",
        };
        watchdog.Start();
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
        var item = new ToolStripMenuItem(Strings.Localize(text)) { ShortcutKeys = shortcut };
        item.Click += (_, _) => action();
        return item;
    }
}
