using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskMonitor;

// 성능 페이지: 왼쪽에 리소스 카드(CPU, 메모리, 디스크 N개, 네트워크 N개, GPU N개),
// 오른쪽에 선택한 항목의 그래프와 자세한 값. CPU는 코어별 그래프, GPU는 엔진별 그래프를 함께 보여 준다.
// 값과 그래프 기록은 항상 쌓고, 화면 그리기는 이 페이지가 보일 때만 한다.
internal sealed class PerformancePage : UserControl
{
    private static readonly Color CpuColor = ColorTranslator.FromHtml("#0078D4");
    private static readonly Color MemoryColor = ColorTranslator.FromHtml("#8764B8");
    private static readonly Color DiskColor = ColorTranslator.FromHtml("#107C10");
    private static readonly Color NicColor = ColorTranslator.FromHtml("#C239B3");
    private static readonly Color GpuColor = ColorTranslator.FromHtml("#D83B01");
    private static readonly Font CardValueFont = new("Segoe UI Variable Text", 8.5F);
    private static readonly Font CardTitleFont = new("Segoe UI Variable Text", 9.5F, FontStyle.Bold);
    private static readonly Font DetailTitleFont = new("Segoe UI Variable Display", 18F);
    private static readonly Font DetailValueFont = new("Segoe UI Variable Display", 22F);
    private static readonly Font DetailStatsFont = new("Segoe UI Variable Text", 9.5F);
    private static readonly Lazy<double> BaseMhz = new(ReadBaseMhz);

    private sealed record Entry(
        string Key,
        string Title,
        string Short,
        string Stats,
        double Percent,
        Color Line,
        IReadOnlyList<(string Label, double Value)> Subs,
        DiskHealthInfo? Health = null,
        GpuDetail? Gpu = null,
        IReadOnlyList<GpuProcessMemory>? Vram = null);

    private sealed class Card
    {
        public required Panel Box { get; init; }
        public required Label Title { get; init; }
        public required Label Value { get; init; }
        public required GraphControl Graph { get; init; }
    }

    private sealed record SubView(GraphControl Graph, Label Text);

    private readonly FlowLayoutPanel _cardPanel = new()
    {
        Dock = DockStyle.Left,
        Width = 248,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(8),
    };
    private readonly Dictionary<string, Card> _cards = new();
    private readonly Dictionary<string, List<double>> _history = new();
    private readonly Dictionary<string, List<double>> _subHistory = new();
    private readonly Label _detailTitle = new() { Dock = DockStyle.Top, Height = 40, Font = DetailTitleFont };
    private readonly Label _detailValue = new() { Dock = DockStyle.Top, Height = 44, Font = DetailValueFont };
    private readonly Label _detailStats = new() { Dock = DockStyle.Top, Height = 150, Font = DetailStatsFont };
    private readonly GraphControl _detailGraph = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _subPanel = new() { Dock = DockStyle.Bottom, Height = 170, AutoScroll = true };
    private readonly RowGrid _smartGrid = new() { Dock = DockStyle.Bottom, Height = 240, Visible = false };
    private readonly RowGrid _gpuGrid = new() { Dock = DockStyle.Bottom, Height = 300, Visible = false };
    private static readonly ColumnDef[] GpuColumns =
    {
        new("item", "항목", 170),
        new("value", "값", 300, Fill: true),
    };
    private Dictionary<int, string> _names = new();
    private static readonly ColumnDef[] SmartColumns =
    {
        new("id", "ID", 48, Right: true),
        new("name", "이름", 180, Fill: true),
        new("current", "현재", 60, Right: true),
        new("worst", "최악", 60, Right: true),
        new("threshold", "임계값", 64, Right: true),
        new("raw", "원시값", 120, Right: true),
        new("status", "상태", 56),
    };
    private readonly List<SubView> _subViews = new();
    private IReadOnlyList<Entry> _entries = Array.Empty<Entry>();
    private string _selected = "cpu";
    private string _subSignature = "";

    public PerformancePage()
    {
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 8) };
        right.Controls.Add(_detailGraph);
        right.Controls.Add(_subPanel);
        right.Controls.Add(_smartGrid);
        right.Controls.Add(_gpuGrid);
        right.Controls.Add(_detailStats);
        right.Controls.Add(_detailValue);
        right.Controls.Add(_detailTitle);

        _smartGrid.SetColumns(SmartColumns);
        _gpuGrid.SetColumns(GpuColumns);
        Controls.Add(right);
        Controls.Add(_cardPanel);
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                Render();
            }
        };
    }

    public void Update(Snapshot snap, PerfSnapshot perf)
    {
        _entries = BuildEntries(snap, perf);
        _names = snap.Processes.GroupBy(p => p.Pid).ToDictionary(group => group.Key, group => group.First().Name);
        foreach (var entry in _entries)
        {
            Push(_history, entry.Key, entry.Percent);
            foreach (var sub in entry.Subs)
            {
                Push(_subHistory, entry.Key + "/" + sub.Label, sub.Value);
            }
        }

        // 사라진 장치(예: 분리된 디스크)의 카드는 치운다.
        var keys = _entries.Select(e => e.Key).ToHashSet();
        foreach (var key in _cards.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _cardPanel.Controls.Remove(_cards[key].Box);
            _cards.Remove(key);
        }

        if (Visible)
        {
            Render();
        }
    }

    private void Render()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        foreach (var entry in _entries)
        {
            if (!_cards.TryGetValue(entry.Key, out var card))
            {
                card = CreateCard(entry.Key);
                _cards[entry.Key] = card;
                _cardPanel.Controls.Add(card.Box);
            }

            card.Title.Text = entry.Title;
            card.Value.Text = entry.Short;
            card.Graph.LineColor = entry.Line;
            card.Graph.FillColor = Tint(entry.Line);
            card.Graph.SetValues(_history[entry.Key]);
        }

        var selected = _entries.FirstOrDefault(e => e.Key == _selected) ?? _entries[0];
        _detailTitle.Text = selected.Title;
        _detailValue.Text = selected.Short;
        _detailStats.Text = selected.Stats;
        _detailGraph.LineColor = selected.Line;
        _detailGraph.FillColor = Tint(selected.Line);
        _detailGraph.SetValues(_history[selected.Key]);
        bool isDisk = selected.Key.StartsWith("disk/", StringComparison.Ordinal);
        _smartGrid.Visible = isDisk;
        if (isDisk)
        {
            RenderSmart(selected.Health);
        }

        bool isGpu = selected.Key.StartsWith("gpu/", StringComparison.Ordinal);
        _gpuGrid.Visible = isGpu;
        // GPU: 설명 영역을 줄이고 표를 늘린다. 두 높이의 합(450px)은 그대로라서 그래프 크기는 바뀌지 않는다.
        _detailStats.Height = isGpu ? 80 : 150;
        _gpuGrid.Height = isGpu ? 370 : 300;
        if (isGpu)
        {
            RenderGpu(selected);
        }

        RenderSubs(selected);
    }

    // 디스크 선택 시: SMART(ATA) 또는 NVMe 상태 항목을 표로 보여 준다.
    private void RenderSmart(DiskHealthInfo? health)
    {
        var rows = new List<Row>();
        foreach (var a in health?.Attributes ?? Array.Empty<SmartAttribute>())
        {
            var row = new Row { Key = a.Id + "/" + a.Name, Fore = a.Bad ? Theme.Red : null };
            row.Cells["id"] = a.Id;
            row.Cells["name"] = a.Name;
            row.Cells["current"] = a.Current;
            row.Cells["worst"] = a.Worst;
            row.Cells["threshold"] = a.Threshold;
            row.Cells["raw"] = a.Raw;
            row.Cells["status"] = Strings.Localize(a.Bad ? "경고" : "정상");
            rows.Add(row);
        }

        _smartGrid.SetRows(rows, true);
    }

    // GPU 선택 시: NVIDIA(NVML) 상세 값, 엔진별 사용률, VRAM을 많이 쓰는 프로세스를 표로 보여 준다.
    private void RenderGpu(Entry selected)
    {
        var rows = new List<Row>();
        static string L(string korean) => Strings.Localize(korean);
        void Add(string key, string item, string value)
        {
            var row = new Row { Key = key };
            row.Cells["item"] = item;
            row.Cells["value"] = value;
            rows.Add(row);
        }

        var g = selected.Gpu;
        if (g is null)
        {
            Add("none", L("상세 정보"), L("NVIDIA 상세 값을 읽지 못했습니다. NVIDIA GPU가 아니거나 드라이버가 없습니다."));
        }
        else
        {
            Add("driver", L("드라이버 버전"), g.DriverVersion);
            Add("temp", L("온도"), Num(g.TemperatureC, "°C"));
            Add("hotspot", L("핫스팟 온도"), Num(g.HotSpotC, "°C"));
            Add("memjunction", L("메모리 접합부 온도"), Num(g.MemoryJunctionC, "°C"));
            Add("fan", L("팬 속도"), Num(g.FanPercent, "%"));
            Add("util", L("GPU 사용률"), Num(g.GpuUtilPercent, "%"));
            Add("memutil", L("메모리 컨트롤러 사용률"), Num(g.MemoryUtilPercent, "%"));
            Add("encoder", L("인코더 사용률"), Num(g.EncoderPercent, "%"));
            Add("decoder", L("디코더 사용률"), Num(g.DecoderPercent, "%"));
            Add("vram", L("VRAM 사용량"),
                g.VramUsedBytes is { } used && g.VramTotalBytes is { } total
                    ? $"{FormatSize(used)} / {FormatSize(total)} ({100.0 * used / total:F0}%)"
                    : "-");
            Add("power", L("전력"),
                g.PowerWatts is { } watts
                    ? $"{watts:F1} W" + (g.PowerLimitWatts is { } limit ? Strings.Fmt(" (제한 {0:F0} W)", limit) : "")
                    : "-");
            Add("clock.gr", L("그래픽 클럭"), Pair(g.GraphicsMHz, g.GraphicsMaxMHz, "MHz"));
            Add("clock.sm", L("SM 클럭"), Num(g.SmMHz, " MHz"));
            Add("clock.mem", L("메모리 클럭"), Num(g.MemoryMHz, " MHz"));
            Add("clock.video", L("영상 클럭"), Num(g.VideoMHz, " MHz"));
            Add("pstate", L("성능 상태"), g.PerformanceState is { } state ? $"P{state}" : "-");
            Add("throttle", L("클럭 제한 원인"), ThrottleText(g.ThrottleReasons));
            Add("pcie", L("PCIe 링크"),
                g.PcieGen is { } gen && g.PcieWidth is { } width
                    ? Strings.Fmt("{0}세대 x{1} (최대 {2}세대 x{3})", gen, width, g.PcieMaxGen, g.PcieMaxWidth)
                    : "-");
            Add("pcie.io", L("PCIe 송신 / 수신"),
                g.PcieTxKbPerSec is { } tx && g.PcieRxKbPerSec is { } rx
                    ? $"{tx / 1024.0:F1} / {rx / 1024.0:F1} MB/s"
                    : "-");
            foreach (var (label, value) in selected.Subs)
            {
                Add("engine/" + label, Strings.Fmt("엔진 {0}", label), $"{value:F1}%");
            }

            // 프로세스별 VRAM: 문서화된 GPU Process Memory 카운터를 우선 쓰고, 없으면 NVML 목록을 쓴다.
            var processes = selected.Vram is { Count: > 0 } vram ? vram : g.Processes;
            foreach (var p in processes.Take(8))
            {
                string who = _names.TryGetValue(p.Pid, out var name) ? name : "PID " + p.Pid;
                Add("proc/" + p.Pid, Strings.Fmt("VRAM 사용: {0} ({1})", who, p.Pid), FormatSize(p.Bytes));
            }

            foreach (var s in g.EncoderSessions)
            {
                Add("enc/" + s.Pid + "/" + s.Codec, Strings.Fmt("인코딩 세션: PID {0}", s.Pid), $"{s.Codec}  {s.Width}x{s.Height}  {s.Fps} fps");
            }

            if (g.EncoderSessions.Count == 0)
            {
                Add("enc", L("인코딩 세션(NVENC)"), L("없음"));
            }

            if (processes.Count == 0)
            {
                Add("proc", L("프로세스별 VRAM"), L("제공되지 않음(WDDM 드라이버는 프로세스별 사용량을 숨길 수 있습니다)"));
            }
        }

        _gpuGrid.SetRows(rows, true);
    }

    // NVML의 클럭 제한 이유 비트와 화면에 보일 이름
    private static readonly (long Mask, string Label)[] ThrottleLabels =
    {
        (0x1, "유휴 상태"),
        (0x2, "애플리케이션 클럭 설정"),
        (0x4, "전력 상한"),
        (0x8, "하드웨어 감속"),
        (0x10, "동기 부스트"),
        (0x20, "온도 감속(소프트웨어)"),
        (0x40, "온도 감속(하드웨어)"),
        (0x80, "전원 브레이크"),
        (0x100, "디스플레이 클럭 설정"),
    };

    private static string ThrottleText(long? bits)
    {
        if (bits is not { } b)
        {
            return "-";
        }

        if (b == 0)
        {
            return Strings.Localize("제한 없음");
        }

        var names = ThrottleLabels.Where(t => (b & t.Mask) != 0).Select(t => Strings.Localize(t.Label)).ToList();
        return names.Count > 0 ? string.Join(", ", names) : Strings.Fmt("알 수 없음(0x{0:X})", b);
    }

    private static string Num(int? value, string unit) => value is { } v ? v + unit : "-";

    private static string Pair(int? current, int? max, string unit) => current is { } c
        ? (max is { } m ? $"{c} / {m} {unit}" : $"{c} {unit}")
        : "-";

    private static string DiskHealthText(DiskHealthInfo? h)
    {
        if (h is null)
        {
            return "\r\n" + Strings.Localize("건강 정보: 이 장치에서 읽지 못했습니다.");
        }

        string temperature = h.TemperatureC is { } t ? $"{t}°C" : "-";
        string life = h.LifeLeftPercent is { } l ? $"{l}%" : "-";
        string hours = h.PowerOnHours is { } p ? Strings.Fmt("{0:N0}시간", p) : "-";
        string cycles = h.PowerCycles is { } c ? Strings.Fmt("{0:N0}회", c) : "-";
        string io = h.HostReadBytes is { } r ? Strings.Fmt("읽기 {0}   쓰기 {1}", FormatSize(r), FormatSize(h.HostWriteBytes ?? 0)) : "";
        return "\r\n"
            + Strings.Fmt("{0} · {1} · 상태 {2}", h.Identity.Model, h.Identity.Bus, h.Verdict) + "\r\n"
            + Strings.Fmt("온도 {0}   남은 수명 {1}   사용 시간 {2}   전원 켬 {3}", temperature, life, hours, cycles) + "\r\n"
            + (io.Length > 0 ? io + "\r\n" : "")
            + Strings.Fmt("펌웨어 {0}   일련번호 {1}", h.Identity.Firmware, h.Identity.Serial)
            + (h.Note.Length > 0 ? "\r\n" + h.Note : "");
    }

    // 선택한 항목의 하위 그래프(CPU 코어, GPU 엔진)
    private void RenderSubs(Entry selected)
    {
        string signature = selected.Key + "|" + string.Join("|", selected.Subs.Select(s => s.Label));
        if (signature != _subSignature)
        {
            _subSignature = signature;
            _subPanel.Controls.Clear();
            _subViews.Clear();
            foreach (var _ in selected.Subs)
            {
                var box = new Panel { Width = 150, Height = 78, Margin = new Padding(4) };
                var graph = new GraphControl { Dock = DockStyle.Fill, LineColor = selected.Line, FillColor = Tint(selected.Line) };
                var label = new Label { Dock = DockStyle.Top, Height = 20, Font = CardValueFont };
                box.Controls.Add(graph);
                box.Controls.Add(label);
                _subPanel.Controls.Add(box);
                _subViews.Add(new SubView(graph, label));
            }
        }

        // GPU는 엔진별 사용률을 상세 표에 함께 보여 주므로 하위 그래프 자리를 비운다.
        _subPanel.Visible = selected.Subs.Count > 0 && !selected.Key.StartsWith("gpu/", StringComparison.Ordinal);
        for (int i = 0; i < selected.Subs.Count && i < _subViews.Count; i++)
        {
            var sub = selected.Subs[i];
            _subViews[i].Text.Text = $"{sub.Label}   {sub.Value:F0}%";
            _subViews[i].Graph.SetValues(_subHistory[selected.Key + "/" + sub.Label]);
        }
    }

    private Card CreateCard(string key)
    {
        var box = new Panel { Width = 224, Height = 96, Margin = new Padding(0, 0, 0, 8), BackColor = Theme.Bg };
        var graph = new GraphControl { Dock = DockStyle.Fill };
        var value = new Label { Dock = DockStyle.Top, Height = 20, Font = CardValueFont };
        var title = new Label { Dock = DockStyle.Top, Height = 22, Font = CardTitleFont };
        box.Controls.Add(graph);
        box.Controls.Add(value);
        box.Controls.Add(title);
        WireClick(box, () =>
        {
            _selected = key;
            Render();
        });
        return new Card { Box = box, Title = title, Value = value, Graph = graph };
    }

    private static IReadOnlyList<Entry> BuildEntries(Snapshot snap, PerfSnapshot perf)
    {
        var list = new List<Entry>();

        double ghz = perf.CpuMhz / 1000;
        double baseGhz = BaseMhz.Value / 1000;
        list.Add(new Entry(
            "cpu",
            "CPU",
            $"{snap.TotalCpuPercent:F0}%   {ghz:F2} GHz",
            Strings.Fmt("사용률 {0:F1}%   사용자 {1:F1}%   커널 {2:F1}%   DPC·인터럽트 {3:F1}%", snap.TotalCpuPercent, snap.UserPercent, snap.KernelPercent, snap.DpcInterruptPercent) + "\r\n"
                + Strings.Fmt("속도 {0:F2} GHz (기준 {1:F2} GHz)   논리 프로세서 {2}개", ghz, baseGhz, snap.LogicalProcessors) + "\r\n"
                + Strings.Fmt("프로세스 {0}개   스레드 {1}개   핸들 {2}개", snap.ProcessCount, snap.TotalThreads, snap.TotalHandles) + "\r\n"
                + Strings.Fmt("가동 시간 {0}", Uptime()),
            snap.TotalCpuPercent,
            CpuColor,
            snap.PerCpuPercent.Select((v, i) => (Strings.Fmt("코어 {0}", i), v)).ToList()));

        var m = perf.Memory;
        double memPercent = m.Total > 0 ? m.Used * 100.0 / m.Total : 0;
        double commitPercent = m.CommitLimit > 0 ? m.Committed * 100.0 / m.CommitLimit : 0;
        list.Add(new Entry(
            "mem",
            Strings.Localize("메모리"),
            $"{memPercent:F0}%   {Gb(m.Used):F1} / {Gb(m.Total):F1} GB",
            Strings.Fmt("사용 중 {0}   사용 가능 {1}   전체 {2}", FormatSize(m.Used), FormatSize(m.Available), FormatSize(m.Total)) + "\r\n"
                + Strings.Fmt("커밋 {0} / {1} ({2:F0}%)", FormatSize(m.Committed), FormatSize(m.CommitLimit), commitPercent) + "\r\n"
                + Strings.Fmt("캐시 {0}   페이지 풀 {1}   비페이지 풀 {2}", FormatSize(m.Cache), FormatSize(m.PagedPool), FormatSize(m.NonPagedPool)),
            memPercent,
            MemoryColor,
            Array.Empty<(string Label, double Value)>()));

        foreach (var d in perf.Disks)
        {
            string capacity = d.TotalBytes > 0
                ? Strings.Fmt("용량 {0} / {1} ({2})", FormatSize(d.TotalBytes - d.FreeBytes), FormatSize(d.TotalBytes), d.Type)
                : Strings.Localize("용량 정보 없음");
            string letters = d.Letters.Length > 0 ? d.Letters : Strings.Localize("드라이브 문자 없음");
            list.Add(new Entry(
                "disk/" + d.Name,
                d.Name,
                $"{d.BusyPercent:F0}%   R {RowBuilders.FormatRate(d.ReadBytesPerSec)}  W {RowBuilders.FormatRate(d.WriteBytesPerSec)}",
                Strings.Fmt("활성 시간 {0:F1}%   응답 시간 {1:F1} ms   큐 길이 {2:F1}", d.BusyPercent, d.ResponseMs, d.QueueLength) + "\r\n"
                    + Strings.Fmt("읽기 {0}   쓰기 {1}", RowBuilders.FormatRate(d.ReadBytesPerSec), RowBuilders.FormatRate(d.WriteBytesPerSec)) + "\r\n"
                    + capacity + Strings.Fmt("   드라이브 {0}", letters)
                    + DiskHealthText(DiskHealthMonitor.Latest.GetValueOrDefault(d.Number)),
                d.BusyPercent,
                DiskColor,
                Array.Empty<(string Label, double Value)>(),
                DiskHealthMonitor.Latest.GetValueOrDefault(d.Number)));
        }

        foreach (var n in perf.Nics)
        {
            list.Add(new Entry(
                "nic/" + n.Name,
                n.Name,
                Strings.Fmt("보냄 {0}  받음 {1}", RowBuilders.FormatRate(n.SendBytesPerSec), RowBuilders.FormatRate(n.ReceiveBytesPerSec)),
                Strings.Fmt("링크 속도 {0:F0} Mbps   사용률 {1:F1}%", n.LinkBitsPerSec / 1_000_000, n.UtilizationPercent) + "\r\n"
                    + Strings.Fmt("보냄 {0}   받음 {1}", RowBuilders.FormatRate(n.SendBytesPerSec), RowBuilders.FormatRate(n.ReceiveBytesPerSec))
                    + TopNetworkPrograms(snap),
                n.UtilizationPercent,
                NicColor,
                Array.Empty<(string Label, double Value)>()));
        }

        var gpuDetails = GpuDetailMonitor.Assign(perf.Gpus);
        foreach (var g in perf.Gpus)
        {
            var engines = g.Engines
                .OrderByDescending(e => e.Value)
                .Select(e => (Label: e.Key, Value: e.Value))
                .ToList();
            string dedicatedTotal = g.DedicatedTotal > 0 ? $" / {FormatSize(g.DedicatedTotal)}" : "";
            list.Add(new Entry(
                "gpu/" + g.Name,
                gpuDetails.GetValueOrDefault(g.Name)?.Name ?? g.Name,
                Strings.Fmt("{0:F0}%   전용 메모리 {1}{2}", g.Utilization, FormatSize(g.DedicatedUsed), dedicatedTotal),
                Strings.Fmt("사용률 {0:F1}% (엔진 중 최대)   전용 메모리 {1}{2}", g.Utilization, FormatSize(g.DedicatedUsed), dedicatedTotal) + "\r\n"
                    + Strings.Fmt("공유 메모리 {0}   커밋 {1}", FormatSize(g.SharedUsed), FormatSize(g.TotalCommitted)) + "\r\n"
                    + string.Join("   ", engines.Take(4).Select(e => $"{e.Label} {e.Value:F0}%")),
                g.Utilization,
                GpuColor,
                engines,
                Gpu: gpuDetails.GetValueOrDefault(g.Name),
                Vram: perf.GpuVramByLuid.GetValueOrDefault(g.Luid)));
        }

        return list;
    }

    // ETW의 프로세스별 속도는 어댑터를 가리지 않으므로 모든 어댑터 합계로 보여 준다. 같은 이름은 합친다.
    private static string TopNetworkPrograms(Snapshot snap)
    {
        if (EtwLatest.State != EtwState.Running)
        {
            return "\r\n\r\n" + Strings.Localize("프로그램별 사용량은 네트워크 측정(ETW)이 켜져 있을 때 보입니다.");
        }

        var rates = EtwLatest.Rates;
        var top = snap.Processes
            .Where(p => rates.ContainsKey(p.Pid))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Sent: g.Sum(p => rates[p.Pid].SentBytesPerSec), Received: g.Sum(p => rates[p.Pid].ReceivedBytesPerSec)))
            .Where(t => t.Sent + t.Received > 0)
            .OrderByDescending(t => t.Sent + t.Received)
            .Take(5)
            .Select(t => "\r\n  " + Strings.Fmt("{0}   받음 {1}  보냄 {2}", t.Name, RowBuilders.FormatRate(t.Received), RowBuilders.FormatRate(t.Sent)));
        return "\r\n\r\n" + Strings.Localize("많이 쓰는 프로그램 (모든 어댑터)") + string.Concat(top);
    }

    private static string Uptime()
    {
        var span = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return Strings.Fmt("{0}일 {1:hh\\:mm\\:ss}", span.Days, span);
    }

    private static void Push(Dictionary<string, List<double>> map, string key, double value)
    {
        if (!map.TryGetValue(key, out var series))
        {
            series = new List<double>();
            map[key] = series;
        }

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

    private static double ReadBaseMhz()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("~MHz") is int mhz ? mhz : 0;
    }

    private static string FormatSize(double bytes) => bytes >= 1073741824
        ? $"{bytes / 1073741824:F2} GB"
        : bytes >= 1048576 ? $"{bytes / 1048576:F1} MB" : $"{bytes / 1024:F0} KB";

    private static double Gb(double bytes) => bytes / 1073741824.0;

    // 그래프 면 색: 선 색을 아주 연하게 바꾼 색
    private static Color Tint(Color c) => Color.FromArgb(64, c);
}
