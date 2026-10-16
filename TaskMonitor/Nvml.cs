using System.Runtime.InteropServices;
using System.Text;

namespace TaskMonitor;

// NVIDIA GPU 상세 값. NVML(nvml.dll, 드라이버에 들어 있는 NVIDIA 공식 관리 라이브러리)을 직접 호출한다.
// nvidia-smi도 같은 라이브러리로 값을 읽는다. 함수 목록과 구조체는 NVIDIA의 NVML API 문서를 따랐고, 다른 프로젝트 코드는 가져오지 않았다.
// 관리자 권한 없이 읽힌다. NVIDIA GPU가 없으면 빈 목록을 돌려준다.

internal sealed record GpuProcessMemory(int Pid, long Bytes);

// 인코딩 세션 하나(NVENC). Codec: H.264, HEVC, AV1 등.
internal sealed record GpuEncoderSession(int Pid, string Codec, int Width, int Height, int Fps);

internal sealed record GpuDetail(
    string Name,
    string DriverVersion,
    int? TemperatureC,
    int? FanPercent,
    double? PowerWatts,
    double? PowerLimitWatts,
    int? GraphicsMHz,
    int? GraphicsMaxMHz,
    int? SmMHz,
    int? MemoryMHz,
    int? VideoMHz,
    int? PerformanceState,
    int? GpuUtilPercent,
    int? MemoryUtilPercent,
    int? EncoderPercent,
    int? DecoderPercent,
    long? VramUsedBytes,
    long? VramTotalBytes,
    int? PcieGen,
    int? PcieWidth,
    int? PcieMaxGen,
    int? PcieMaxWidth,
    long? PcieTxKbPerSec,
    long? PcieRxKbPerSec,
    IReadOnlyList<GpuProcessMemory> Processes,
    long? ThrottleReasons,
    IReadOnlyList<GpuEncoderSession> EncoderSessions,
    int? HotSpotC,
    int? MemoryJunctionC,
    // PCI 버스·장치 번호: GPU의 물리 위치. PDH 카드(LUID)와 NVAPI 온도를 짝짓는 기준이다. NVIDIA가 아니면 없다.
    int? PciBus = null,
    int? PciDevice = null);

internal static class Nvml
{
    private const int Success = 0;
    private const int InsufficientSize = 7;
    private const ulong NotAvailable = ulong.MaxValue;
    private const int ClockGraphics = 0;
    private const int ClockSm = 1;
    private const int ClockMemory = 2;
    private const int ClockVideo = 3;
    private const int TemperatureGpu = 0;
    private const uint PcieTx = 0;
    private const uint PcieRx = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public uint Pid;
        public ulong UsedGpuMemory;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EncoderSessionInfo
    {
        public uint SessionId;
        public uint Pid;
        public uint VgpuInstance;
        public uint CodecType;
        public uint HResolution;
        public uint VResolution;
        public uint AverageFps;
        public uint AverageLatency;
    }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temperature);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint speed);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint clock);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint clock);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPerformanceState(IntPtr device, out uint state);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEncoderUtilization(IntPtr device, out uint utilization, out uint samplingPeriodUs);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetDecoderUtilization(IntPtr device, out uint utilization, out uint samplingPeriodUs);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out MemoryInfo memory);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint generation);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr device, out uint generation);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxPcieLinkWidth(IntPtr device, out uint width);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPcieThroughput(IntPtr device, uint counter, out uint kbPerSec);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetComputeRunningProcesses_v3(IntPtr device, ref uint count, [In, Out] ProcessInfo[]? infos);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetGraphicsRunningProcesses_v3(IntPtr device, ref uint count, [In, Out] ProcessInfo[]? infos);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrentClocksEventReasons(IntPtr device, out ulong reasons);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEncoderSessions(IntPtr device, ref uint sessionCount, [In, Out] EncoderSessionInfo[]? infos);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, IntPtr info);

    // nvmlPciInfo_t 크기는 넉넉히 잡는다. 버스·장치 번호는 domain 다음 칸(오프셋 20, 24)에 있다.
    private const int PciInfoBufferSize = 256;
    private const int PciBusOffset = 20;
    private const int PciDeviceOffset = 24;

    private static bool _initialized;

    public static IReadOnlyList<GpuDetail> ReadAll()
    {
        var list = new List<GpuDetail>();
        try
        {
            if (!_initialized)
            {
                if (nvmlInit_v2() != Success)
                {
                    return list;
                }

                _initialized = true;
            }

            if (nvmlDeviceGetCount_v2(out uint count) != Success)
            {
                return list;
            }

            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out IntPtr device) == Success)
                {
                    list.Add(ReadOne(device));
                }
            }
        }
        catch (DllNotFoundException)
        {
            // NVIDIA 드라이버가 없는 PC: 빈 목록
        }
        catch (EntryPointNotFoundException)
        {
            // 오래된 드라이버: 빈 목록
        }

        return list;
    }

    private static GpuDetail ReadOne(IntPtr device)
    {
        var name = new StringBuilder(96);
        nvmlDeviceGetName(device, name, (uint)name.Capacity);
        var driver = new StringBuilder(96);
        nvmlSystemGetDriverVersion(driver, (uint)driver.Capacity);

        int? temperature = ToInt(nvmlDeviceGetTemperature(device, TemperatureGpu, out uint t), t);
        int? fan = ToInt(nvmlDeviceGetFanSpeed(device, out uint fanSpeed), fanSpeed);
        double? power = ToWatts(nvmlDeviceGetPowerUsage(device, out uint mw), mw);
        double? powerLimit = ToWatts(nvmlDeviceGetPowerManagementLimit(device, out uint limitMw), limitMw);
        int? graphics = Clock(device, ClockGraphics, max: false);
        int? graphicsMax = Clock(device, ClockGraphics, max: true);
        int? sm = Clock(device, ClockSm, max: false);
        int? memoryClock = Clock(device, ClockMemory, max: false);
        int? video = Clock(device, ClockVideo, max: false);
        int? pstate = ToInt(nvmlDeviceGetPerformanceState(device, out uint ps), ps);

        int? gpuUtil = null, memUtil = null;
        if (nvmlDeviceGetUtilizationRates(device, out Utilization u) == Success)
        {
            gpuUtil = (int)u.Gpu;
            memUtil = (int)u.Memory;
        }

        int? encoder = ToInt(nvmlDeviceGetEncoderUtilization(device, out uint enc, out _), enc);
        int? decoder = ToInt(nvmlDeviceGetDecoderUtilization(device, out uint dec, out _), dec);

        long? vramUsed = null, vramTotal = null;
        if (nvmlDeviceGetMemoryInfo(device, out MemoryInfo m) == Success)
        {
            vramUsed = (long)m.Used;
            vramTotal = (long)m.Total;
        }

        int? pcieGen = ToInt(nvmlDeviceGetCurrPcieLinkGeneration(device, out uint pg), pg);
        int? pcieWidth = ToInt(nvmlDeviceGetCurrPcieLinkWidth(device, out uint pw), pw);
        int? pcieMaxGen = ToInt(nvmlDeviceGetMaxPcieLinkGeneration(device, out uint pmg), pmg);
        int? pcieMaxWidth = ToInt(nvmlDeviceGetMaxPcieLinkWidth(device, out uint pmw), pmw);
        long? tx = ToLong(nvmlDeviceGetPcieThroughput(device, PcieTx, out uint txv), txv);
        long? rx = ToLong(nvmlDeviceGetPcieThroughput(device, PcieRx, out uint rxv), rxv);
        var pci = PciLocation(device);

        return new GpuDetail(
            name.ToString(),
            driver.ToString(),
            temperature,
            fan,
            power,
            powerLimit,
            graphics,
            graphicsMax,
            sm,
            memoryClock,
            video,
            pstate,
            gpuUtil,
            memUtil,
            encoder,
            decoder,
            vramUsed,
            vramTotal,
            pcieGen,
            pcieWidth,
            pcieMaxGen,
            pcieMaxWidth,
            tx,
            rx,
            Processes(device),
            ThrottleReasons(device),
            EncoderSessions(device),
            null,
            null,
            pci?.Bus,
            pci?.Device);
    }

    // PCI 위치(버스, 장치). 드라이버가 오래되어 함수가 없으면 null.
    private static (int Bus, int Device)? PciLocation(IntPtr device)
    {
        IntPtr buffer = Marshal.AllocHGlobal(PciInfoBufferSize);
        try
        {
            if (nvmlDeviceGetPciInfo_v3(device, buffer) != Success)
            {
                return null;
            }

            return (Marshal.ReadInt32(buffer, PciBusOffset), Marshal.ReadInt32(buffer, PciDeviceOffset));
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // 클럭이 제한되는 이유(비트 마스크). 값의 뜻은 NVML의 clocks event reason 상수와 같다.
    private static long? ThrottleReasons(IntPtr device)
    {
        try
        {
            return nvmlDeviceGetCurrentClocksEventReasons(device, out ulong reasons) == Success ? (long)reasons : null;
        }
        catch (EntryPointNotFoundException)
        {
            // 오래된 드라이버: 제한 원인을 모른다
            return null;
        }
    }

    // NVENC 인코딩 세션 목록. 두 번 호출한다(첫 호출은 개수만 받는다).
    private static IReadOnlyList<GpuEncoderSession> EncoderSessions(IntPtr device)
    {
        try
        {
            uint count = 0;
            int rc = nvmlDeviceGetEncoderSessions(device, ref count, null);
            if ((rc != Success && rc != InsufficientSize) || count == 0)
            {
                return Array.Empty<GpuEncoderSession>();
            }

            var infos = new EncoderSessionInfo[count];
            rc = nvmlDeviceGetEncoderSessions(device, ref count, infos);
            if (rc != Success)
            {
                return Array.Empty<GpuEncoderSession>();
            }

            return infos
                .Take((int)count)
                .Select(s => new GpuEncoderSession((int)s.Pid, CodecName(s.CodecType), (int)s.HResolution, (int)s.VResolution, (int)s.AverageFps))
                .ToList();
        }
        catch (EntryPointNotFoundException)
        {
            return Array.Empty<GpuEncoderSession>();
        }
    }

    private static string CodecName(uint type) => type switch
    {
        0 => "H.264",
        1 => "HEVC",
        2 => "AV1",
        _ => $"코덱 {type}",
    };

    private static int? ToInt(int rc, uint value) => rc == Success ? (int)value : null;

    private static long? ToLong(int rc, uint value) => rc == Success ? (long)value : null;

    private static double? ToWatts(int rc, uint milliwatts) => rc == Success ? milliwatts / 1000.0 : null;

    private static int? Clock(IntPtr device, int type, bool max)
    {
        uint value;
        int rc = max
            ? nvmlDeviceGetMaxClockInfo(device, type, out value)
            : nvmlDeviceGetClockInfo(device, type, out value);
        return ToInt(rc, value);
    }

    // 그래픽 프로세스와 컴퓨트 프로세스의 VRAM 사용량을 합친다. 같은 프로세스가 두 목록에 모두 있으면 큰 값을 쓴다.
    // WDDM(일반 화면 드라이버 모델)에서는 프로세스별 사용량이 제공되지 않을 수 있어서 그때는 목록이 비어 있다.
    private static IReadOnlyList<GpuProcessMemory> Processes(IntPtr device)
    {
        var byPid = new Dictionary<int, long>();
        AddProcesses(byPid, device, compute: true);
        AddProcesses(byPid, device, compute: false);
        return byPid
            .Select(kv => new GpuProcessMemory(kv.Key, kv.Value))
            .OrderByDescending(p => p.Bytes)
            .ToList();
    }

    private static void AddProcesses(Dictionary<int, long> byPid, IntPtr device, bool compute)
    {
        try
        {
            // 첫 호출은 개수만 받는다(INSUFFICIENT_SIZE가 정상 응답). 두 번째 호출에서 실제 목록을 받는다.
            uint count = 0;
            int rc = compute
                ? nvmlDeviceGetComputeRunningProcesses_v3(device, ref count, null)
                : nvmlDeviceGetGraphicsRunningProcesses_v3(device, ref count, null);
            if (rc != Success && rc != InsufficientSize)
            {
                return;
            }

            if (count == 0)
            {
                return;
            }

            var infos = new ProcessInfo[count];
            rc = compute
                ? nvmlDeviceGetComputeRunningProcesses_v3(device, ref count, infos)
                : nvmlDeviceGetGraphicsRunningProcesses_v3(device, ref count, infos);
            if (rc != Success)
            {
                return;
            }

            foreach (var info in infos.Take((int)count))
            {
                if (info.UsedGpuMemory == NotAvailable)
                {
                    continue;
                }

                int pid = (int)info.Pid;
                long bytes = (long)info.UsedGpuMemory;
                byPid[pid] = Math.Max(byPid.GetValueOrDefault(pid), bytes);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // 프로세스 목록 함수가 없는 드라이버: 이 목록만 비운다
        }
    }
}

// GPU 상세 값을 주기적으로 읽는 백그라운드 스레드. 화면은 마지막 결과만 쓴다.
internal static class GpuDetailMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static IReadOnlyList<GpuDetail> _latest = Array.Empty<GpuDetail>();
    private static int _started;

    public static IReadOnlyList<GpuDetail> Latest => Volatile.Read(ref _latest);

    // PDH의 GPU 카드와 장치를 짝짓는다.
    //  1) 카드 LUID → PCI 위치(버스·장치 번호)가 장치의 PCI 위치와 같으면 짝이다. 카드 순서나 이름에 기대지 않는다.
    //  2) 위치를 알 수 없는 카드는 드라이버 이름이 카드 이름에 들어 있으면 그것으로 맞춘다.
    //  3) 그래도 남은 카드와 장치는 전용 메모리 사용량이 가장 가까운 쌍으로 맞춘다(오차 256MB 또는 20% 이내일 때만).
    public static IReadOnlyDictionary<string, GpuDetail> Assign(IReadOnlyList<GpuStats> cards)
    {
        var devices = Latest.ToList();
        var result = new Dictionary<string, GpuDetail>();
        var unlocated = new List<GpuStats>();
        foreach (var card in cards)
        {
            // 위치가 NVML 장치와 맞지 않는 카드(예: AMD)는 다음 단계로 넘긴다.
            var byLocation = GpuLocation.Find(card.Luid) is { } location
                ? devices.FirstOrDefault(d => d.PciBus == location.Bus && d.PciDevice == location.Device)
                : null;
            if (byLocation is not null)
            {
                result[card.Name] = byLocation;
                devices.Remove(byLocation);
            }
            else
            {
                unlocated.Add(card);
            }
        }

        foreach (var card in unlocated.ToList())
        {
            var named = devices.FirstOrDefault(d =>
                card.Name.Contains(d.Name, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(card.Name, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                continue;
            }

            result[card.Name] = named;
            devices.Remove(named);
            unlocated.Remove(card);
        }

        foreach (var device in devices)
        {
            long used = device.VramUsedBytes ?? 0;
            var best = unlocated.OrderBy(c => Math.Abs(c.DedicatedUsed - used)).FirstOrDefault();
            if (best is null || Math.Abs(best.DedicatedUsed - used) > Math.Max(256L * 1024 * 1024, used * 0.2))
            {
                continue;
            }

            result[best.Name] = device;
            unlocated.Remove(best);
        }

        return result;
    }

    // NVAPI 온도를 NVIDIA 장치에 붙인다. 버스 번호를 알면 같은 버스의 장치끼리 맞춘다.
    // 버스 번호를 못 얻은 경우에만 목록 순서에 기대는 예전 방식을 쓴다.
    private static IReadOnlyList<GpuDetail> MergeThermal(IReadOnlyList<GpuDetail> devices, IReadOnlyList<NvapiThermal> thermal)
    {
        bool byBus = thermal.Any(t => t.Bus is not null);
        var result = new List<GpuDetail>();
        int next = 0;
        foreach (var device in devices)
        {
            if (!device.Name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(device);
                continue;
            }

            NvapiThermal? t = byBus
                ? thermal.FirstOrDefault(x => device.PciBus is int bus && x.Bus == bus)
                : next < thermal.Count ? thermal[next++] : null;
            result.Add(t is null ? device : device with { HotSpotC = t.HotSpotC, MemoryJunctionC = t.MemoryJunctionC });
        }

        return result;
    }

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var devices = Nvml.ReadAll().Concat(Adl.ReadAll()).ToList();
                    var nvidia = devices.Where(d => d.Name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase)).ToList();
                    Volatile.Write(ref _latest, MergeThermal(devices, Nvapi.ReadAll(nvidia)));
                }
                catch (Exception)
                {
                    // 한 번 실패해도 다음 주기에 다시 읽는다.
                }

                Thread.Sleep(Interval);
            }
        })
        {
            IsBackground = true,
            Name = "TaskMonitor GPU detail",
        };
        thread.Start();
    }
}
