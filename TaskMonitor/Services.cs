using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskMonitor;

internal sealed record ServiceRow(string Name, string DisplayName, int ProcessId, uint State);

// 서비스 목록(PID 포함)과 시작/중지/다시 시작.
internal static class ServiceList
{
    private const int ErrorMoreData = 234;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const uint ScManagerConnectAndEnumerate = 0x0005;
    private const uint ServiceControlStop = 0x0001;
    private const uint ServiceStateStopped = 0x0001;

    internal static string StateName(uint state) => state switch
    {
        1 => "중지됨",
        2 => "시작 중",
        3 => "중지 중",
        4 => "실행 중",
        5 => "계속 시작 중",
        6 => "일시 중지 중",
        7 => "일시 중지됨",
        _ => "알 수 없음",
    };

    internal static List<ServiceRow> Enumerate()
    {
        IntPtr scm = Win32.OpenSCManagerW(null, null, Win32.ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var result = new List<ServiceRow>();
            uint resume = 0;
            uint size = 0x10000;
            int entrySize = Marshal.SizeOf<Win32.EnumServiceStatusProcess>();

            while (true)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    bool ok = Win32.EnumServicesStatusExW(scm, 0, Win32.ServiceWin32, Win32.ServiceStateAll,
                        buffer, size, out _, out uint count, ref resume, null);
                    int error = Marshal.GetLastWin32Error();
                    if (!ok && error != ErrorMoreData)
                    {
                        throw new Win32Exception(error);
                    }

                    for (int i = 0; i < count; i++)
                    {
                        var e = Marshal.PtrToStructure<Win32.EnumServiceStatusProcess>(IntPtr.Add(buffer, i * entrySize));
                        result.Add(new ServiceRow(
                            Marshal.PtrToStringUni(e.ServiceName) ?? "",
                            Marshal.PtrToStringUni(e.DisplayName) ?? "",
                            (int)e.ProcessId,
                            e.CurrentState));
                    }

                    if (ok)
                    {
                        return result;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            Win32.CloseServiceHandle(scm);
        }
    }

    internal static void Start(string name)
    {
        WithService(name, Win32.ServiceStart | Win32.ServiceQueryStatus, StartHandle);
    }

    internal static void Stop(string name)
    {
        WithService(name, Win32.ServiceStop | Win32.ServiceQueryStatus, StopHandle);
    }

    internal static void Restart(string name)
    {
        WithService(name, Win32.ServiceStart | Win32.ServiceStop | Win32.ServiceQueryStatus, svc =>
        {
            StopHandle(svc);
            WaitForStopped(svc, TimeSpan.FromSeconds(15));
            StartHandle(svc);
        });
    }

    private static void WithService(string name, uint access, Action<IntPtr> action)
    {
        IntPtr scm = Win32.OpenSCManagerW(null, null, ScManagerConnectAndEnumerate);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            IntPtr service = Win32.OpenServiceW(scm, name, access);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                action(service);
            }
            finally
            {
                Win32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Win32.CloseServiceHandle(scm);
        }
    }

    private static void StartHandle(IntPtr service)
    {
        if (!Win32.StartServiceW(service, 0, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceAlreadyRunning)
            {
                throw new Win32Exception(error);
            }
        }
    }

    private static void StopHandle(IntPtr service)
    {
        if (!Win32.ControlService(service, ServiceControlStop, out _))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceNotActive)
            {
                throw new Win32Exception(error);
            }
        }
    }

    private static void WaitForStopped(IntPtr service, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!Win32.QueryServiceStatus(service, out var status))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (status.CurrentState == ServiceStateStopped)
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("서비스가 제한 시간 안에 멈추지 않았습니다.");
    }

    // 시작 유형: 2 자동, 3 수동, 4 사용 안 함. 바꾸려면 보통 관리자 권한이 필요하다(실패하면 오류로 알린다).
    internal const uint StartAuto = 2, StartManual = 3, StartDisabled = 4;

    internal static uint? StartType(string name)
    {
        uint? type = null;
        try
        {
            WithService(name, Win32.ServiceQueryConfig, svc =>
            {
                Win32.QueryServiceConfigW(svc, IntPtr.Zero, 0, out uint needed);
                IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (Win32.QueryServiceConfigW(svc, buffer, needed, out _))
                    {
                        type = (uint)Marshal.ReadInt32(buffer, 4); // QUERY_SERVICE_CONFIG.dwStartType
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            });
        }
        catch (Win32Exception)
        {
            // 읽을 권한이 없으면 표시만 안 한다.
        }

        return type;
    }

    internal static void SetStartType(string name, uint startType)
    {
        WithService(name, Win32.ServiceChangeConfig, svc =>
        {
            if (!Win32.ChangeServiceConfigW(svc, Win32.ServiceNoChange, startType, Win32.ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        });
    }

    internal static void OpenServicesConsole()
    {
        Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
    }
}
