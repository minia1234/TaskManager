using System.Runtime.InteropServices;

namespace TaskMonitor;

// NVIDIA 핫스팟·메모리 접합부 온도: NVAPI(nvapi64.dll, 드라이버에 들어 있음)를 nvapi_QueryInterface로 불러 호출한다.
// 함수 ID와 구조체 배치(센서 32개, 온도 단위 1/256 °C, RTX 40의 핫스팟은 1번, 메모리 접합부는 7번 칸)는
// LibreHardwareMonitor(MPL-2.0)의 NVAPI 선언을 공개 API 사실로 참고했다. 코드는 새로 작성했다.
// 여러 GPU는 PCI 버스 번호(NvAPI_GPU_GetBusId)로 NVML 장치와 짝짓는다. 목록 순서는 버스 번호를 못 얻을 때만 쓴다.
// 다른 세대(RTX 50 등)의 칸 배치는 이 PC에서 검증하지 못했다.
internal sealed record NvapiThermal(int Index, string Name, int? HotSpotC, int? MemoryJunctionC, int? Bus);

internal static class Nvapi
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetThermalSensors = 0x65FE3AAD;
    private const uint IdGetBusId = 0x1BE0B8E5;
    private const int MaxPhysicalGpus = 64;
    private const int ThermalSensorStructVersion = 2;
    private const int Ok = 0;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] handles, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetThermalSensorsDelegate(IntPtr handle, ref ThermalSensors sensors);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBusIdDelegate(IntPtr handle, out uint busId);

    // NvThermalSensors: 버전, 마스크, 예약 8칸, 온도 32칸(1/256 °C 고정소수점). 크기는 168바이트다.
    [StructLayout(LayoutKind.Sequential)]
    private struct ThermalSensors
    {
        public uint Version;
        public uint Mask;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public int[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public int[] Temperatures;
    }

    private static bool _initialized;
    private static bool _unavailable;
    private static InitializeDelegate? _initialize;
    private static EnumPhysicalGpusDelegate? _enumGpus;
    private static GetThermalSensorsDelegate? _getThermalSensors;
    private static GetBusIdDelegate? _getBusId;
    private static readonly Dictionary<IntPtr, uint> SensorMasks = new();

    // 마지막 상태(자체 검증과 진단용)
    internal static string Status { get; private set; } = "아직 호출하지 않음";

    // nvidia: NVML이 본 NVIDIA 장치. 버스 번호로 짝짓고, 시리즈(RTX 40 등)를 이름에서 고르는 데 쓴다.
    public static IReadOnlyList<NvapiThermal> ReadAll(IReadOnlyList<GpuDetail> nvidia)
    {
        var list = new List<NvapiThermal>();
        if (_unavailable)
        {
            return list;
        }

        try
        {
            if (!Initialize())
            {
                return list;
            }

            var handles = new IntPtr[MaxPhysicalGpus];
            if (_enumGpus!(handles, out int count) != Ok)
            {
                Status = "NvAPI_EnumPhysicalGPUs 실패";
                return list;
            }

            Status = $"초기화 성공, NVAPI GPU {count}개";
            for (int i = 0; i < count; i++)
            {
                int? bus = ReadBus(handles[i]);
                GpuDetail? match = bus is int b
                    ? nvidia.FirstOrDefault(d => d.PciBus == b)
                    : i < nvidia.Count ? nvidia[i] : null;
                list.Add(ReadOne(i, handles[i], match?.Name ?? "", bus));
            }
        }
        catch (DllNotFoundException)
        {
            Status = "nvapi64.dll 없음(NVIDIA 드라이버 미설치)";
            _unavailable = true;
        }
        catch (EntryPointNotFoundException)
        {
            Status = "필요한 NVAPI 함수 없음";
            _unavailable = true;
        }

        return list;
    }

    private static bool Initialize()
    {
        if (_initialized)
        {
            return true;
        }

        IntPtr initPtr = QueryInterface(IdInitialize);
        IntPtr enumPtr = QueryInterface(IdEnumPhysicalGpus);
        IntPtr sensorsPtr = QueryInterface(IdGetThermalSensors);
        IntPtr busPtr = QueryInterface(IdGetBusId);
        if (initPtr == IntPtr.Zero || enumPtr == IntPtr.Zero || sensorsPtr == IntPtr.Zero)
        {
            Status = "NVAPI 함수 ID를 찾지 못함";
            _unavailable = true;
            return false;
        }

        _initialize = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr);
        _enumGpus = Marshal.GetDelegateForFunctionPointer<EnumPhysicalGpusDelegate>(enumPtr);
        _getThermalSensors = Marshal.GetDelegateForFunctionPointer<GetThermalSensorsDelegate>(sensorsPtr);
        // 버스 번호 함수가 없으면 짝짓기만 목록 순서로 한다(온도는 그대로 읽는다).
        _getBusId = busPtr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<GetBusIdDelegate>(busPtr);

        int rc = _initialize();
        if (rc != Ok)
        {
            Status = $"NvAPI_Initialize 실패(코드 {rc})";
            _unavailable = true;
            return false;
        }

        _initialized = true;
        return true;
    }

    // PCI 버스 번호. 실패하면 null.
    private static int? ReadBus(IntPtr handle)
    {
        if (_getBusId is null)
        {
            return null;
        }

        return _getBusId(handle, out uint bus) == Ok ? (int)bus : null;
    }

    private static NvapiThermal ReadOne(int index, IntPtr handle, string name, int? bus)
    {
        // 센서 마스크는 GPU마다 바뀌지 않는다. 매초 다시 찾으면 드라이버 호출이 최대 32번 늘어난다.
        if (!SensorMasks.TryGetValue(handle, out uint mask))
        {
            mask = FindSensorMask(handle);
            SensorMasks[handle] = mask;
        }

        var sensors = NewSensors(mask);
        if (mask == 0 || _getThermalSensors!(handle, ref sensors) != Ok)
        {
            return new NvapiThermal(index, name, null, null, bus);
        }

        // RTX 50 계열은 핫스팟을 이 방식으로 주지 않는다(LibreHardwareMonitor 기준). 그 외는 1번 칸.
        bool rtx50 = name.Contains("RTX 50", StringComparison.OrdinalIgnoreCase);
        bool rtx40 = name.Contains("RTX 40", StringComparison.OrdinalIgnoreCase);
        int? hotSpot = rtx50 ? null : Celsius(sensors.Temperatures[1]);
        int memoryIndex = rtx50 ? 2 : rtx40 ? 7 : 9;
        int? memoryJunction = Celsius(sensors.Temperatures[memoryIndex]);
        return new NvapiThermal(index, name, hotSpot, memoryJunction, bus);
    }

    // 센서 마스크: 비트를 0번부터 올리며 NVAPI가 거절하는 첫 비트 바로 아래까지 켠다(LibreHardwareMonitor와 같은 방식).
    private static uint FindSensorMask(IntPtr handle)
    {
        for (int bit = 0; bit < 32; bit++)
        {
            var probe = NewSensors(1u << bit);
            if (_getThermalSensors!(handle, ref probe) != Ok)
            {
                return bit == 0 ? 0 : (1u << bit) - 1;
            }
        }

        return uint.MaxValue;
    }

    private static ThermalSensors NewSensors(uint mask) => new()
    {
        Version = (uint)(Marshal.SizeOf<ThermalSensors>() | (ThermalSensorStructVersion << 16)),
        Mask = mask,
        Reserved = new int[8],
        Temperatures = new int[32],
    };

    private static int? Celsius(int raw) => raw == 0 ? null : raw / 256;

    // 구조체 크기 검사용(자체 검증): 헤더 기준 168바이트
    internal static int ThermalSensorsSize() => Marshal.SizeOf<ThermalSensors>();
}
