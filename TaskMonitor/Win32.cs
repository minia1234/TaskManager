using System.Runtime.InteropServices;
using System.Text;

namespace TaskMonitor;

// 작업 관리자 기능(우선 순위, 선호도, 효율 모드, 덤프, 창 전환, 세션, 서비스)에 쓰는 Win32 호출 모음.
internal static class Win32
{
    internal const uint ProcessTerminate = 0x0001;
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessSetInformation = 0x0200;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    internal const uint PriorityIdle = 0x0040;
    internal const uint PriorityBelowNormal = 0x4000;
    internal const uint PriorityNormal = 0x0020;
    internal const uint PriorityAboveNormal = 0x8000;
    internal const uint PriorityHigh = 0x0080;
    internal const uint PriorityRealtime = 0x0100;

    private const int ProcessPowerThrottlingClass = 4;
    private const int ProcessCommandLineInformationClass = 60;
    private const int TokenUserClass = 1;
    private const uint TokenQuery = 0x0008;

    internal const uint MiniDumpWithFullMemory = 0x00000002;

    internal const uint ScManagerEnumerateService = 0x0004;
    internal const uint ServiceStart = 0x0010;
    internal const uint ServiceStop = 0x0020;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const uint ServiceWin32 = 0x30;
    internal const uint ServiceStateAll = 0x3;

    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenServiceW(IntPtr scManager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool StartServiceW(IntPtr service, uint argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    // 시스템 전체 메모리 정보 (GetPerformanceInfo). 작업 관리자 "메모리" 페이지의 값과 같은 출처다.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    internal static extern bool GetPerformanceInfo(out PerformanceInformation info, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    // 열린 핸들이 가리키는 프로세스의 생성 시각(FILETIME, 100ns 단위). PID 재사용을 가려낼 때 쓴다.
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr processMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    // 조회할 때도 info.Version을 1로 미리 채워야 한다(아니면 ERROR_INVALID_PARAMETER).
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool LookupAccountSidW(string? system, IntPtr sid, StringBuilder name, ref uint nameLength, StringBuilder domain, ref uint domainLength, out int use);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("dbghelp.dll", SetLastError = true)]
    internal static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool WTSEnumerateSessionsW(IntPtr server, uint reserved, uint version, out IntPtr sessions, out uint count);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSDisconnectSession(IntPtr server, uint sessionId, bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSLogoffSession(IntPtr server, uint sessionId, bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool WTSSendMessageW(IntPtr server, uint sessionId, string title, uint titleLength, string message, uint messageLength, uint style, uint timeout, out uint response, bool wait);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool EnumServicesStatusExW(IntPtr scManager, int infoLevel, uint serviceType, uint serviceState, IntPtr buffer, uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? group);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    // 앞 창의 프로세스 ID와, 그 창이 주 화면 전체를 덮는지(전체 화면). 창이 없으면 (0, false).
    internal static (int ProcessId, bool Fullscreen) ForegroundWindowState()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return (0, false);
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (!GetWindowRect(hwnd, out var rect))
        {
            return ((int)pid, false);
        }

        bool fullscreen = rect.Left <= 0 && rect.Top <= 0
            && rect.Right - rect.Left >= GetSystemMetrics(SmCxScreen)
            && rect.Bottom - rect.Top >= GetSystemMetrics(SmCyScreen);
        return ((int)pid, fullscreen);
    }

    // 현재 프로세스 PID의 창 중 보이는 첫 창을 찾는다.
    internal static IntPtr FindVisibleWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == processId && IsWindowVisible(hwnd) && GetWindowTextLengthW(hwnd) > 0)
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    internal static bool ActivateWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        return SetForegroundWindow(hwnd);
    }

    internal static string QueryFullImagePath(IntPtr process)
    {
        var sb = new StringBuilder(1024);
        uint size = (uint)sb.Capacity;
        return QueryFullProcessImageNameW(process, 0, sb, ref size) ? sb.ToString(0, (int)size) : "";
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> UserNames = new();

    private static string LookupName(IntPtr sid)
    {
        var name = new StringBuilder(256);
        var domain = new StringBuilder(256);
        uint nameLen = (uint)name.Capacity, domainLen = (uint)domain.Capacity;
        if (!LookupAccountSidW(null, sid, name, ref nameLen, domain, ref domainLen, out _))
        {
            return "";
        }

        return domainLen > 0 ? $"{domain}\\{name}" : name.ToString();
    }

    // 토큰의 사용자 계정 이름 (도메인\사용자). 권한이 없으면 빈 문자열.
    internal static string QueryUserName(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out IntPtr token))
        {
            return "";
        }

        try
        {
            GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out int needed);
            if (needed <= 0)
            {
                return "";
            }

            IntPtr info = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenUserClass, info, needed, out _))
                {
                    return "";
                }

                IntPtr sid = Marshal.ReadIntPtr(info, 0);
                if (!ConvertSidToStringSidW(sid, out IntPtr sidText))
                {
                    return "";
                }

                try
                {
                    string key = Marshal.PtrToStringUni(sidText) ?? "";
                    // 같은 SID는 한 번만 찾는다. 도메인 계정 조회는 네트워크를 기다릴 수 있어서 반복하면 느려진다.
                    return UserNames.GetOrAdd(key, _ => LookupName(sid));
                }
                finally
                {
                    LocalFree(sidText);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // 명령줄 전체 문자열 (PROCESS_QUERY_LIMITED_INFORMATION 으로 조회)
    internal static string QueryCommandLine(IntPtr process)
    {
        int size = 4096;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQueryInformationProcess(process, ProcessCommandLineInformationClass, buffer, size, out int returned);
                if (status == 0)
                {
                    var str = Marshal.PtrToStructure<UnicodeString>(buffer);
                    return str.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(str.Buffer, str.Length / 2) ?? "";
                }

                if (status != unchecked((int)0xC0000004))
                {
                    return "";
                }

                size = Math.Max(returned, size * 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return "";
    }

    // 효율 모드(절전 스로틀링) 상태: ControlMask와 StateMask의 실행 속도 비트가 모두 켜져 있으면 켜짐
    internal static bool IsPowerThrottled(IntPtr process)
    {
        var state = new PowerThrottlingState { Version = 1 };
        if (!GetProcessInformation(process, ProcessPowerThrottlingClass, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
        {
            return false;
        }

        return (state.ControlMask & 1) != 0 && (state.StateMask & 1) != 0;
    }

    internal static void SetPowerThrottling(IntPtr process, bool enabled)
    {
        var state = new PowerThrottlingState
        {
            Version = 1,
            ControlMask = enabled ? 1u : 0u,
            StateMask = enabled ? 1u : 0u,
        };
        if (!SetProcessInformation(process, ProcessPowerThrottlingClass, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
        {
            throw new InvalidOperationException($"효율 모드 설정 실패 (오류 {Marshal.GetLastWin32Error()})");
        }
    }

    internal static uint ProcessDumpAccess => ProcessQueryInformation | ProcessVmRead;
}
