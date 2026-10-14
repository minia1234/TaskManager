using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskMonitor;

internal enum PriorityLevel
{
    Realtime,
    High,
    AboveNormal,
    Normal,
    BelowNormal,
    Idle,
}

// 프로세스 대상 작업. 실패하면 Win32Exception 또는 InvalidOperationException을 던진다.
internal static class ProcessActions
{
    internal static string PriorityName(PriorityLevel level) => level switch
    {
        PriorityLevel.Realtime => "실시간",
        PriorityLevel.High => "높음",
        PriorityLevel.AboveNormal => "높은 우선 순위 이상",
        PriorityLevel.Normal => "보통",
        PriorityLevel.BelowNormal => "보통 이하",
        _ => "낮음",
    };

    private static uint PriorityClass(PriorityLevel level) => level switch
    {
        PriorityLevel.Realtime => Win32.PriorityRealtime,
        PriorityLevel.High => Win32.PriorityHigh,
        PriorityLevel.AboveNormal => Win32.PriorityAboveNormal,
        PriorityLevel.Normal => Win32.PriorityNormal,
        PriorityLevel.BelowNormal => Win32.PriorityBelowNormal,
        _ => Win32.PriorityIdle,
    };

    internal static void EndTask(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill();
    }

    // 자식 프로세스를 먼저 끝내고 대상을 끝낸다. 실패한 개수를 돌려준다.
    internal static int EndTree(int pid, IReadOnlyList<ProcessUsage> all)
    {
        var children = all.ToLookup(p => p.ParentPid);
        var visited = new HashSet<int>();
        int failures = 0;

        void Kill(int id)
        {
            if (!visited.Add(id))
            {
                return;
            }

            foreach (var child in children[id])
            {
                Kill(child.Pid);
            }

            try
            {
                EndTask(id);
            }
            catch (Exception)
            {
                failures++;
            }
        }

        Kill(pid);
        return failures;
    }

    internal static PriorityLevel GetPriority(int pid)
    {
        IntPtr handle = OpenOrThrow(pid, Win32.ProcessQueryLimitedInformation);
        try
        {
            uint cls = Win32.GetPriorityClass(handle);
            return cls switch
            {
                Win32.PriorityRealtime => PriorityLevel.Realtime,
                Win32.PriorityHigh => PriorityLevel.High,
                Win32.PriorityAboveNormal => PriorityLevel.AboveNormal,
                Win32.PriorityBelowNormal => PriorityLevel.BelowNormal,
                Win32.PriorityIdle => PriorityLevel.Idle,
                _ => PriorityLevel.Normal,
            };
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    internal static void SetPriority(int pid, PriorityLevel level)
    {
        IntPtr handle = OpenOrThrow(pid, Win32.ProcessSetInformation | Win32.ProcessQueryLimitedInformation);
        try
        {
            if (!Win32.SetPriorityClass(handle, PriorityClass(level)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    internal static ulong GetAffinity(int pid)
    {
        IntPtr handle = OpenOrThrow(pid, Win32.ProcessQueryInformation | Win32.ProcessQueryLimitedInformation);
        try
        {
            if (!Win32.GetProcessAffinityMask(handle, out UIntPtr processMask, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return processMask.ToUInt64();
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    internal static void SetAffinity(int pid, ulong mask)
    {
        IntPtr handle = OpenOrThrow(pid, Win32.ProcessSetInformation | Win32.ProcessQueryInformation);
        try
        {
            if (!Win32.SetProcessAffinityMask(handle, new UIntPtr(mask)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    internal static bool IsEfficiencyMode(int pid)
    {
        IntPtr handle = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Win32.IsPowerThrottled(handle);
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    // 효율 모드: 우선 순위를 낮음으로 바꾼 뒤 절전 스로틀링을 켠다. 끄면 보통으로 되돌리고 스로틀링을 푼다.
    // 순서가 중요하다: 우선 순위를 나중에 바꾸면 스로틀링 상태가 초기화된다.
    internal static void SetEfficiencyMode(int pid, bool on)
    {
        IntPtr handle = OpenOrThrow(pid, Win32.ProcessSetInformation | Win32.ProcessQueryLimitedInformation);
        try
        {
            if (!Win32.SetPriorityClass(handle, on ? Win32.PriorityIdle : Win32.PriorityNormal))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            Win32.SetPowerThrottling(handle, on);
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    // 전체 메모리 덤프(.dmp)를 임시 폴더에 쓰고 경로를 돌려준다.
    internal static string WriteDump(int pid, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "TaskMonitorDumps");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}.{pid}.dmp");

        IntPtr handle = OpenOrThrow(pid, Win32.ProcessDumpAccess);
        try
        {
            bool ok;
            int error;
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ok = Win32.MiniDumpWriteDump(handle, (uint)pid, file.SafeFileHandle.DangerousGetHandle(),
                    Win32.MiniDumpWithFullMemory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                error = Marshal.GetLastWin32Error();
            }

            if (!ok)
            {
                File.Delete(path);
                throw new Win32Exception(error);
            }

            return path;
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    internal static void OpenFileLocation(string path)
    {
        if (path.Length == 0)
        {
            throw new InvalidOperationException("파일 경로를 알 수 없습니다.");
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    internal static void ShowProperties(string path)
    {
        if (path.Length == 0)
        {
            throw new InvalidOperationException("파일 경로를 알 수 없습니다.");
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "properties" });
    }

    internal static void SearchOnline(string text)
    {
        Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={Uri.EscapeDataString(text)}") { UseShellExecute = true });
    }

    // 보이는 창이 있으면 앞으로 가져온다. 창이 없으면 false.
    internal static bool SwitchTo(int pid)
    {
        IntPtr hwnd = Win32.FindVisibleWindow(pid);
        return hwnd != IntPtr.Zero && Win32.ActivateWindow(hwnd);
    }

    internal static void RunNewTask(string command, bool asAdmin)
    {
        var info = new ProcessStartInfo(command) { UseShellExecute = true };
        if (asAdmin)
        {
            info.Verb = "runas";
        }

        Process.Start(info);
    }

    private static IntPtr OpenOrThrow(int pid, uint access)
    {
        IntPtr handle = Win32.OpenProcess(access, false, pid);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }
}
