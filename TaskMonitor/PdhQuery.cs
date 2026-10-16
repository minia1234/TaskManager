using System.Runtime.InteropServices;

namespace TaskMonitor;

// PDH(성능 카운터) 직접 호출. 와일드카드 경로(예: \GPU Engine(*)\Utilization Percentage)는
// 한 번의 수집으로 모든 인스턴스 값을 읽는다. 작업 관리자도 같은 방식(PdhGetFormattedCounterArrayW)을 쓴다.
// 영문 경로를 써서 Windows 표시 언어와 관계없이 동작한다.
internal sealed class PdhQuery : IDisposable
{
    private const uint FmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhSuccess = 0;
    private const uint CStatusValidData = 0;
    private const uint CStatusNewData = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct FmtValue
    {
        public uint CStatus;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FmtItem
    {
        public IntPtr Name;
        public FmtValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, out uint bufferSize, out uint itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);

    private readonly IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new(StringComparer.OrdinalIgnoreCase);

    public PdhQuery()
    {
        uint status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != PdhSuccess)
        {
            throw new InvalidOperationException($"PdhOpenQuery 실패: 0x{status:X8}");
        }
    }

    // 카운터가 없는 환경(드라이버 미설치 등)이면 false를 돌려주고, 그 항목은 빈 값으로 둔다.
    public bool Add(string path)
    {
        if (_counters.ContainsKey(path))
        {
            return true;
        }

        if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) != PdhSuccess)
        {
            return false;
        }

        _counters[path] = counter;
        return true;
    }

    public void Collect() => PdhCollectQueryData(_query);

    // 인스턴스 이름 -> 값. 추가되지 않은 경로는 빈 사전을 돌려준다.
    public Dictionary<string, double> ReadArray(string path)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!_counters.TryGetValue(path, out var counter))
        {
            return result;
        }

        uint size = 0, count = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, FmtDouble, out size, out count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0)
        {
            return result;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, FmtDouble, out size, out count, buffer);
            if (status != PdhSuccess)
            {
                return result;
            }

            int itemSize = Marshal.SizeOf<FmtItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<FmtItem>(IntPtr.Add(buffer, i * itemSize));
                string name = Marshal.PtrToStringUni(item.Name) ?? "";
                if (item.Value.CStatus is CStatusValidData or CStatusNewData)
                {
                    result[name] = item.Value.Value;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    public double ReadSingle(string path)
    {
        if (!_counters.TryGetValue(path, out var counter))
        {
            return 0;
        }

        return PdhGetFormattedCounterValue(counter, FmtDouble, out _, out var value) == PdhSuccess ? value.Value : 0;
    }

    public void Dispose()
    {
        PdhCloseQuery(_query);
    }
}
