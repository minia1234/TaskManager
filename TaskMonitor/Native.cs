using System.Runtime.InteropServices;

namespace TaskMonitor;

// ntdll 시스템 정보 조회. 작업 관리자(taskmgr)가 쓰는 것과 같은 API다.
// 구조체는 x64 오프셋으로 직접 읽는다.
internal static class Native
{
    internal const int SystemProcessInformation = 5;
    internal const int SystemProcessorPerformanceInformation = 8;

    private const uint StatusSuccess = 0;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, uint length, out uint returnLength);

    // 버퍼를 할당해 반환한다. 호출자가 Marshal.FreeHGlobal로 해제해야 한다.
    // bufferSize는 다음 호출의 힌트로 재사용되고, 실제로 받은 길이는 returnLength로 돌려준다.
    internal static IntPtr Query(int infoClass, ref int bufferSize, out int returnLength)
    {
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            uint status = NtQuerySystemInformation(infoClass, buffer, (uint)bufferSize, out uint needed);
            if (status == StatusSuccess)
            {
                returnLength = (int)needed;
                return buffer;
            }

            Marshal.FreeHGlobal(buffer);
            if (status != StatusInfoLengthMismatch)
            {
                throw new InvalidOperationException($"NtQuerySystemInformation({infoClass}) 실패: 0x{status:X8}");
            }

            // 조회 사이에 프로세스가 늘어날 수 있어서 여유를 두고 다시 시도한다.
            bufferSize = (int)Math.Max(needed, (uint)bufferSize) + 0x4000;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static MemoryStatusEx ReadMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref status);
        return status;
    }

    // 작업 관리자 "메모리" 머리글에 표시되는 사용률(%)
    internal static int MemoryLoadPercent() => (int)ReadMemoryStatus().MemoryLoad;

    internal static (long Total, long Available) MemoryBytes()
    {
        var status = ReadMemoryStatus();
        return ((long)status.TotalPhys, (long)status.AvailPhys);
    }
}
