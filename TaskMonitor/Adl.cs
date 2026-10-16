using System.Runtime.InteropServices;

namespace TaskMonitor;

// AMD GPU 값: ADL(AMD Display Library)의 atiadlxx.dll을 런타임에 불러 호출한다. AMD 드라이버가 설치한 DLL이며, 우리는 DLL을 배포하지 않는다.
// 함수 원형과 구조체 크기는 AMD 공개 ADL SDK(샘플, 헤더)와 LibreHardwareMonitor(MPL-2.0)의 interop 선언을 공개 API 사실로 참고했다.
// 단위: 온도는 천분의 도(ADLTemperature), 클럭은 0.01 곱해서 MHz, 전력은 >> 8 해서 W(LibreHardwareMonitor 방식), VRAM은 MB.
// AMD GPU가 없는 PC에서는 DLL이 없어서 빈 목록을 돌려주고, 다시 시도하지 않는다.
// 이 코드는 AMD 실제 하드웨어에서 검증하지 못했다.
internal static class Adl
{
    private const int Ok = 0;
    private const string Dll = "atiadlxx.dll";
    private const int OdnTotalPower = 0; // ADLODNCurrentPowerType.ODN_GPU_TOTAL_POWER

    public delegate IntPtr MemoryAllocDelegate(int size);

    // ADLTemperature: 두 개의 int. Value는 천분의 도(millidegree) 섭씨
    [StructLayout(LayoutKind.Sequential)]
    private struct Temperature
    {
        public int Size;
        public int Value;
    }

    // ADLPMActivity: 열 개의 int. 클럭 단위는 0.01 MHz, 사용률은 0~100
    [StructLayout(LayoutKind.Sequential)]
    private struct PmActivity
    {
        public int Size;
        public int EngineClock;
        public int MemoryClock;
        public int Vddc;
        public int ActivityPercent;
        public int CurrentPerformanceLevel;
        public int CurrentBusSpeed;
        public int CurrentBusLanes;
        public int MaximumBusLanes;
        public int Reserved;
    }

    // 콜백은 ADL이 메모리를 요청할 때 쓴다. 델리게이트가 수집되지 않도록 정적 필드로 잡아 둔다.
    private static readonly MemoryAllocDelegate Allocator = size => Marshal.AllocHGlobal(size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Create(MemoryAllocDelegate callback, int connectedAdapters, ref IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, ref int numAdapters);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_Active_Get(IntPtr context, int adapterIndex, out int status);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_DedicatedVRAMUsage_Get(IntPtr context, int adapterIndex, out int usageMb);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Overdrive5_Temperature_Get(IntPtr context, int adapterIndex, int thermalControllerIndex, ref Temperature temperature);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Overdrive5_CurrentActivity_Get(IntPtr context, int adapterIndex, ref PmActivity activity);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Overdrive6_CurrentPower_Get(IntPtr context, int adapterIndex, int powerType, ref int value);

    private static IntPtr _context;
    private static bool _initialized;
    private static bool _unavailable;

    // 마지막 상태(자체 검증과 진단에 쓴다): DLL 없음, 초기화 실패 코드, 어댑터 개수 등
    internal static string Status { get; private set; } = "아직 호출하지 않음";

    public static IReadOnlyList<GpuDetail> ReadAll()
    {
        var list = new List<GpuDetail>();
        if (_unavailable)
        {
            return list;
        }

        try
        {
            if (!_initialized)
            {
                int rc = ADL2_Main_Control_Create(Allocator, 1, ref _context);
                if (rc != Ok)
                {
                    Status = $"초기화 실패(코드 {rc})";
                    _unavailable = true;
                    return list;
                }

                _initialized = true;
            }

            int count = 0;
            if (ADL2_Adapter_NumberOfAdapters_Get(_context, ref count) != Ok)
            {
                Status = "어댑터 개수 조회 실패";
                return list;
            }

            Status = $"초기화 성공, 어댑터 {count}개";
            for (int i = 0; i < count; i++)
            {
                if (ADL2_Adapter_Active_Get(_context, i, out int active) == Ok && active != 0)
                {
                    list.Add(ReadOne(i));
                }
            }
        }
        catch (DllNotFoundException)
        {
            Status = "atiadlxx.dll 없음(AMD 드라이버 미설치)";
            _unavailable = true;
        }
        catch (EntryPointNotFoundException)
        {
            Status = "필요한 ADL 함수 없음";
            _unavailable = true;
        }

        return list;
    }

    // 구조체 크기 검사용: 헤더에 적힌 필드 수와 맞는지 확인한다.
    internal static (int Temperature, int Activity) StructSizes() =>
        (Marshal.SizeOf<Temperature>(), Marshal.SizeOf<PmActivity>());

    private static GpuDetail ReadOne(int index)
    {
        int? temperatureC = null;
        var temp = new Temperature { Size = Marshal.SizeOf<Temperature>() };
        if (ADL2_Overdrive5_Temperature_Get(_context, index, 0, ref temp) == Ok)
        {
            temperatureC = temp.Value / 1000;
        }

        int? graphicsMhz = null, memoryMhz = null, utilization = null, lanes = null, maxLanes = null;
        var activity = new PmActivity { Size = Marshal.SizeOf<PmActivity>() };
        if (ADL2_Overdrive5_CurrentActivity_Get(_context, index, ref activity) == Ok)
        {
            graphicsMhz = (int)(activity.EngineClock * 0.01);
            memoryMhz = (int)(activity.MemoryClock * 0.01);
            utilization = Math.Min(activity.ActivityPercent, 100);
            lanes = activity.CurrentBusLanes;
            maxLanes = activity.MaximumBusLanes;
        }

        double? power = null;
        int powerValue = 0;
        if (ADL2_Overdrive6_CurrentPower_Get(_context, index, OdnTotalPower, ref powerValue) == Ok)
        {
            power = powerValue >> 8;
        }

        long? vramUsed = null;
        if (ADL2_Adapter_DedicatedVRAMUsage_Get(_context, index, out int usageMb) == Ok)
        {
            vramUsed = (long)usageMb * 1024 * 1024;
        }

        return new GpuDetail(
            Name: $"AMD Radeon (ADL {index})",
            DriverVersion: "-",
            TemperatureC: temperatureC,
            FanPercent: null,
            PowerWatts: power,
            PowerLimitWatts: null,
            GraphicsMHz: graphicsMhz,
            GraphicsMaxMHz: null,
            SmMHz: null,
            MemoryMHz: memoryMhz,
            VideoMHz: null,
            PerformanceState: null,
            GpuUtilPercent: utilization,
            MemoryUtilPercent: null,
            EncoderPercent: null,
            DecoderPercent: null,
            VramUsedBytes: vramUsed,
            VramTotalBytes: null,
            PcieGen: null,
            PcieWidth: lanes,
            PcieMaxGen: null,
            PcieMaxWidth: maxLanes,
            PcieTxKbPerSec: null,
            PcieRxKbPerSec: null,
            Processes: Array.Empty<GpuProcessMemory>(),
            ThrottleReasons: null,
            EncoderSessions: Array.Empty<GpuEncoderSession>(),
            HotSpotC: null,
            MemoryJunctionC: null);
    }
}
