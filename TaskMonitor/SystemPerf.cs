using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TaskMonitor;

internal sealed record MemoryStats(long Total, long Available, long Committed, long CommitLimit, long Cache, long PagedPool, long NonPagedPool)
{
    public long Used => Total - Available;
}

internal sealed record DiskStats(
    string Name,
    string Letters,
    string Type,
    long TotalBytes,
    long FreeBytes,
    double BusyPercent,
    double ReadBytesPerSec,
    double WriteBytesPerSec,
    double ResponseMs,
    double QueueLength,
    int Number);

internal sealed record NicStats(string Name, double LinkBitsPerSec, double SendBytesPerSec, double ReceiveBytesPerSec, double UtilizationPercent);

internal sealed record GpuStats(
    string Name,
    double Utilization,
    IReadOnlyDictionary<string, double> Engines,
    long DedicatedUsed,
    long SharedUsed,
    long TotalCommitted,
    long DedicatedTotal,
    string Luid);

internal sealed record PerfSnapshot(
    MemoryStats Memory,
    double CpuUtilityPercent,
    double CpuMhz,
    IReadOnlyList<DiskStats> Disks,
    IReadOnlyList<NicStats> Nics,
    IReadOnlyList<GpuStats> Gpus,
    IReadOnlyDictionary<int, double> GpuByPid,
    IReadOnlyDictionary<string, IReadOnlyList<GpuProcessMemory>> GpuVramByLuid)
{
    public static readonly PerfSnapshot Empty = new(
        new MemoryStats(0, 0, 0, 0, 0, 0, 0),
        0,
        0,
        Array.Empty<DiskStats>(),
        Array.Empty<NicStats>(),
        Array.Empty<GpuStats>(),
        new Dictionary<int, double>(),
        new Dictionary<string, IReadOnlyList<GpuProcessMemory>>(StringComparer.OrdinalIgnoreCase));
}

// 성능 페이지용 시스템 값. PDH 와일드카드 카운터로 디스크, 네트워크, GPU 전체를 한 번에 읽는다.
// 백그라운드 스레드가 1초마다 값을 만들고, UI는 가장 최근 값(Latest)만 읽는다.
internal sealed class SystemPerf : IDisposable
{
    private const string CpuUtility = @"\Processor Information(_Total)\% Processor Utility";
    private const string CpuFrequency = @"\Processor Information(_Total)\Processor Frequency";
    private const string DiskBusy = @"\PhysicalDisk(*)\% Disk Time";
    private const string DiskRead = @"\PhysicalDisk(*)\Disk Read Bytes/sec";
    private const string DiskWrite = @"\PhysicalDisk(*)\Disk Write Bytes/sec";
    private const string DiskQueue = @"\PhysicalDisk(*)\Current Disk Queue Length";
    private const string DiskResponse = @"\PhysicalDisk(*)\Avg. Disk sec/Transfer";
    private const string NicReceive = @"\Network Interface(*)\Bytes Received/sec";
    private const string NicSend = @"\Network Interface(*)\Bytes Sent/sec";
    private const string NicBandwidth = @"\Network Interface(*)\Current Bandwidth";
    private const string GpuEngine = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string GpuShared = @"\GPU Adapter Memory(*)\Shared Usage";
    private const string GpuCommitted = @"\GPU Adapter Memory(*)\Total Committed";
    private const string GpuProcessDedicated = @"\GPU Process Memory(*)\Dedicated Usage";

    private static readonly Regex EngineRegex = new(@"pid_(\d+)_luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.+)$", RegexOptions.Compiled);
    private static readonly Regex AdapterRegex = new(@"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(\d+)", RegexOptions.Compiled);
    private static readonly string[] Counters =
    {
        CpuUtility, CpuFrequency, DiskBusy, DiskRead, DiskWrite, DiskQueue, DiskResponse,
        NicReceive, NicSend, NicBandwidth, GpuEngine, GpuDedicated, GpuShared, GpuCommitted, GpuProcessDedicated,
    };
    // "GPU Process Memory" 인스턴스 이름: pid_<번호>_luid_<어댑터>_phys_<번호>
    private static readonly Regex ProcessVramRegex = new(@"pid_(\d+)_luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_\d+", RegexOptions.Compiled);
    private static readonly Lazy<List<(string Name, long Vram)>> DisplayAdapters = new(ReadDisplayAdapters);

    private readonly CancellationTokenSource _cts = new();
    private volatile PerfSnapshot? _latest;
    private PdhQuery? _pdh;

    // 가장 최근에 측정한 값. 아직 측정 전이면 빈 값.
    public PerfSnapshot Latest => _latest ?? PerfSnapshot.Empty;

    public void Start()
    {
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    // 동기 측정(자체 검증용). 화면은 Latest를 쓴다.
    public PerfSnapshot Sample() => Measure();

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            long lastMeasure = 0;
            while (await timer.WaitForNextTickAsync(token))
            {
                // 창이 안 보이면 5초마다만 잰다(자원 기록 주기와 같다). 그래프용 매초 측정은 필요 없다.
                if (AppState.Background && Environment.TickCount64 - lastMeasure < 5000)
                {
                    continue;
                }

                lastMeasure = Environment.TickCount64;
                _latest = Measure();
            }
        }
        catch (OperationCanceledException)
        {
            // 앱 종료 신호
        }
        finally
        {
            _pdh?.Dispose();
        }
    }

    private PerfSnapshot Measure()
    {
        EnsureQuery();
        _pdh?.Collect();

        double cpuUtility = Math.Clamp(ReadSingle(CpuUtility), 0, 100);
        double cpuMhz = ReadSingle(CpuFrequency);
        var (gpus, byPid) = BuildGpu();
        return new PerfSnapshot(ReadMemory(), cpuUtility, cpuMhz, BuildDisks(), BuildNics(), gpus, byPid, BuildGpuVram());
    }

    private void EnsureQuery()
    {
        if (_pdh is not null)
        {
            return;
        }

        try
        {
            _pdh = new PdhQuery();
            foreach (var path in Counters)
            {
                _pdh.Add(path);
            }
        }
        catch (InvalidOperationException)
        {
            _pdh = null;
        }
    }

    private double ReadSingle(string path) => _pdh?.ReadSingle(path) ?? 0;

    private Dictionary<string, double> Read(string path) =>
        _pdh?.ReadArray(path) ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    // 메모리: GetPerformanceInfo (작업 관리자와 같은 출처)
    private static MemoryStats ReadMemory()
    {
        if (!Win32.GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<Win32.PerformanceInformation>()))
        {
            return new MemoryStats(0, 0, 0, 0, 0, 0, 0);
        }

        long page = (long)info.PageSize;
        return new MemoryStats(
            (long)info.PhysicalTotal * page,
            (long)info.PhysicalAvailable * page,
            (long)info.CommitTotal * page,
            (long)info.CommitLimit * page,
            (long)info.SystemCache * page,
            (long)info.KernelPaged * page,
            (long)info.KernelNonpaged * page);
    }

    // 프로세스별 전용 VRAM(WDDM): "GPU Process Memory" 카운터를 어댑터 LUID별로 묶는다.
    // 값이 0인 프로세스는 뺀다. 같은 드라이버 모델의 D3DKMT 조회와 같은 종류의 값이지만, 문서화된 카운터라서 구조체를 추측하지 않는다.
    private IReadOnlyDictionary<string, IReadOnlyList<GpuProcessMemory>> BuildGpuVram()
    {
        var byLuid = new Dictionary<string, List<GpuProcessMemory>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, bytes) in Read(GpuProcessDedicated))
        {
            var match = ProcessVramRegex.Match(instance);
            if (!match.Success || bytes <= 0)
            {
                continue;
            }

            string luid = match.Groups[2].Value;
            if (!byLuid.TryGetValue(luid, out var list))
            {
                list = new List<GpuProcessMemory>();
                byLuid[luid] = list;
            }

            list.Add(new GpuProcessMemory(int.Parse(match.Groups[1].Value), (long)bytes));
        }

        return byLuid.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<GpuProcessMemory>)kv.Value.OrderByDescending(p => p.Bytes).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    // 디스크: 물리 디스크마다 하나씩. 인스턴스 이름은 "번호 드라이브문자:" 형식이다.
    private List<DiskStats> BuildDisks()
    {
        var busy = Read(DiskBusy);
        var read = Read(DiskRead);
        var write = Read(DiskWrite);
        var queue = Read(DiskQueue);
        var response = Read(DiskResponse);

        var disks = new List<DiskStats>();
        foreach (var (instance, busyPercent) in busy)
        {
            if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = instance.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string id = parts.Length > 0 ? parts[0] : instance;
            string letters = parts.Length > 1 ? parts[1].Trim() : "";
            var (total, free, type) = SizeOf(letters);
            string name = Strings.Fmt("디스크 {0}", id) + (letters.Length > 0 ? $" ({letters})" : "");
            disks.Add(new DiskStats(
                name,
                letters,
                type,
                total,
                free,
                Math.Clamp(busyPercent, 0, 100),
                Value(read, instance),
                Value(write, instance),
                Value(response, instance) * 1000,
                Value(queue, instance),
                int.TryParse(id, out int number) ? number : -1));
        }

        return disks.OrderBy(d => d.Name, StringComparer.CurrentCulture).ToList();
    }

    private static (long Total, long Free, string Type) SizeOf(string letters)
    {
        long total = 0, free = 0;
        string type = "";
        foreach (var token in letters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var drive = new DriveInfo(token + "\\");
                if (!drive.IsReady)
                {
                    continue;
                }

                total += drive.TotalSize;
                free += drive.AvailableFreeSpace;
                if (type.Length == 0)
                {
                    type = drive.DriveType switch
                    {
                        DriveType.Fixed => "내장 디스크",
                        DriveType.Removable => "이동식",
                        DriveType.Network => "네트워크",
                        DriveType.CDRom => "광학",
                        _ => "기타",
                    };
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // 드라이브 문자가 없거나 준비되지 않은 경우는 건너뛴다.
            }
        }

        return (total, free, type);
    }

    // 네트워크: 인터페이스마다 하나씩. 연결 속도 대비 송수신 사용률을 계산한다.
    private List<NicStats> BuildNics()
    {
        var receive = Read(NicReceive);
        var send = Read(NicSend);
        var bandwidth = Read(NicBandwidth);

        var nics = new List<NicStats>();
        foreach (var (instance, bits) in bandwidth)
        {
            if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase) || bits <= 0)
            {
                continue;
            }

            double rx = Value(receive, instance), tx = Value(send, instance);
            double utilization = Math.Clamp((rx + tx) * 8 / bits * 100, 0, 100);
            nics.Add(new NicStats(instance, bits, tx, rx, utilization));
        }

        return nics.OrderBy(n => n.Name, StringComparer.CurrentCulture).ToList();
    }

    // GPU: 엔진 인스턴스(pid_..._luid_..._phys_..._engtype_...)를 어댑터별, 엔진 종류별로 합친다.
    // 어댑터 사용률은 엔진 중 가장 높은 값(작업 관리자와 같은 방식)이다.
    private (List<GpuStats> Gpus, Dictionary<int, double> ByPid) BuildGpu()
    {
        var engines = Read(GpuEngine);
        var byPid = new Dictionary<int, double>();
        var adapters = new Dictionary<string, (int Phys, Dictionary<string, double> Engines)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (instance, value) in engines)
        {
            var match = EngineRegex.Match(instance);
            if (!match.Success)
            {
                continue;
            }

            int pid = int.Parse(match.Groups[1].Value);
            byPid[pid] = byPid.GetValueOrDefault(pid) + value;

            string luid = match.Groups[2].Value;
            int phys = int.Parse(match.Groups[3].Value);
            if (!adapters.TryGetValue(luid, out var adapter))
            {
                adapter = (phys, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
                adapters[luid] = adapter;
            }

            string engineType = match.Groups[5].Value;
            adapter.Engines[engineType] = adapter.Engines.GetValueOrDefault(engineType) + value;
        }

        var dedicated = AdapterMemory(GpuDedicated);
        var shared = AdapterMemory(GpuShared);
        var committed = AdapterMemory(GpuCommitted);
        var names = DisplayAdapters.Value;

        var gpus = new List<GpuStats>();
        int index = 0;
        // 물리 번호(Phys)가 같은 어댑터가 둘일 수 있으므로 이름은 목록 순서로 붙인다(중복되면 카드가 뒤섞인다).
        foreach (var (luid, adapter) in adapters.OrderBy(a => a.Value.Phys).ThenBy(a => a.Key, StringComparer.Ordinal))
        {
            var engineValues = adapter.Engines.ToDictionary(e => e.Key, e => Math.Clamp(e.Value, 0, 100), StringComparer.OrdinalIgnoreCase);
            double utilization = engineValues.Count > 0 ? engineValues.Values.Max() : 0;

            // 어댑터 이름과 전체 VRAM은 레지스트리 순서와 맞출 수 있을 때만 붙인다.
            string name = $"GPU {index}";
            long vram = 0;
            if (names.Count == adapters.Count && index < names.Count)
            {
                name = $"GPU {index} ({names[index].Name})";
                vram = names[index].Vram;
            }

            gpus.Add(new GpuStats(
                name,
                utilization,
                engineValues,
                dedicated.GetValueOrDefault(luid),
                shared.GetValueOrDefault(luid),
                committed.GetValueOrDefault(luid),
                vram,
                luid));
            index++;
        }

        var clamped = byPid.ToDictionary(p => p.Key, p => Math.Clamp(p.Value, 0, 100));
        return (gpus, clamped);
    }

    private Dictionary<string, long> AdapterMemory(string path)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, bytes) in Read(path))
        {
            var match = AdapterRegex.Match(instance);
            if (match.Success)
            {
                result[match.Groups[1].Value] = (long)bytes;
            }
        }

        return result;
    }

    // 디스플레이 어댑터 목록(이름, 전용 VRAM). 레지스트리 클래스 키에서 읽는다.
    private static List<(string Name, long Vram)> ReadDisplayAdapters()
    {
        var result = new List<(string Name, long Vram)>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root is null)
            {
                return result;
            }

            foreach (var sub in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)).OrderBy(n => n))
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string name)
                {
                    continue;
                }

                long vram = key.GetValue("HardwareInformation.qwMemorySize") is long bytes ? bytes : 0;
                result.Add((name, vram));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 레지스트리를 못 읽으면 어댑터 이름 없이 번호만 보여준다.
        }

        return result;
    }

    private static double Value(Dictionary<string, double> values, string instance) =>
        values.TryGetValue(instance, out var value) ? value : 0;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
