using System.Globalization;
using System.Runtime.InteropServices;

namespace TaskMonitor;

// 어댑터 LUID(PDH 카운터 이름에 있는 "0x..._0x..." 값)를 PCI 버스·장치 번호로 바꾼다.
// LUID는 같은 GPU에 항상 같은 값이라서, 카드 목록 순서나 VRAM 크기 대신 이 위치로 NVML·NVAPI 장치와 짝짓는다.
// D3DKMT(gdi32.dll, WDDM 커널 인터페이스)의 어댑터 주소 질의(KMTQAITYPE_ADAPTERADDRESS)를 쓴다.
internal static class GpuLocation
{
    private const int AdapterAddressQuery = 6;
    private const int Ok = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapterFromLuid
    {
        public Luid AdapterLuid;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr PrivateData;
        public uint PrivateDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterAddress
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapter args);

    private static readonly Dictionary<string, (int Bus, int Device)> Cache = new(StringComparer.OrdinalIgnoreCase);

    // 성공한 결과만 기억한다(LUID가 바뀌면 새 값으로 다시 찾는다). 실패하면 null.
    public static (int Bus, int Device)? Find(string luidText)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(luidText, out var cached))
            {
                return cached;
            }
        }

        var found = Query(luidText);
        if (found is { } location)
        {
            lock (Cache)
            {
                Cache[luidText] = location;
            }
        }

        return found;
    }

    private static (int Bus, int Device)? Query(string luidText)
    {
        // 형식: 0x<상위 8자리>_0x<하위 8자리>
        var parts = luidText.Split('_');
        if (parts.Length != 2 ||
            !uint.TryParse(parts[0].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high) ||
            !uint.TryParse(parts[1].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low))
        {
            return null;
        }

        var open = new OpenAdapterFromLuid { AdapterLuid = new Luid { LowPart = low, HighPart = unchecked((int)high) } };
        if (D3DKMTOpenAdapterFromLuid(ref open) != Ok)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AdapterAddress>());
        try
        {
            var query = new QueryAdapterInfo
            {
                Adapter = open.Adapter,
                Type = AdapterAddressQuery,
                PrivateData = buffer,
                PrivateDataSize = (uint)Marshal.SizeOf<AdapterAddress>(),
            };
            if (D3DKMTQueryAdapterInfo(ref query) != Ok)
            {
                return null;
            }

            var address = Marshal.PtrToStructure<AdapterAddress>(buffer);
            return ((int)address.BusNumber, (int)address.DeviceNumber);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            var close = new CloseAdapter { Adapter = open.Adapter };
            D3DKMTCloseAdapter(ref close);
        }
    }
}
