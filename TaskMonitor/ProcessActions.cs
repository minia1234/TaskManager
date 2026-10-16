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

// 프로세스 식별자: PID와 생성 시각. PID는 끝난 뒤 다른 프로그램이 다시 쓸 수 있어서 둘을 함께 본다.
internal readonly record struct ProcessKey(int Pid, long CreateTime);

// 프로세스 대상 작업. 실패하면 Win32Exception 또는 InvalidOperationException을 던진다.
internal static class ProcessActions
{
    // 효율 모드를 켜기 전 우선 순위. 끌 때 이 값으로 되돌린다(프로그램을 다시 시작하면 기억이 없어서 보통으로 돌아간다).
    private static readonly Dictionary<ProcessKey, uint> SavedPriority = new();

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

    // 지금 PID의 식별자(생성 시각 포함). 자체 검증에서 기록과 맞는지 확인할 때 쓴다.
    internal static ProcessKey KeyOf(int pid)
    {
        IntPtr handle = OpenOrThrow(pid, Win32.ProcessQueryLimitedInformation);
        try
        {
            if (!Win32.GetProcessTimes(handle, out long created, out _, out _, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new ProcessKey(pid, created);
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    // 끝내기: 핸들을 연 뒤 생성 시각을 확인하고, 같은 핸들로 끝낸다. 이미 끝나서 PID가 다른 프로그램으로 바뀌었으면 건드리지 않는다.
    internal static void EndTask(ProcessKey key)
    {
        if (key.Pid == Environment.ProcessId)
        {
            throw new InvalidOperationException("Task Monitor 자신은 끝낼 수 없습니다.");
        }

        IntPtr handle = OpenVerified(key, Win32.ProcessTerminate);
        try
        {
            if (!Win32.TerminateProcess(handle, 1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    // 자식 프로세스를 먼저 끝내고 대상을 끝낸다. 자식은 같은 PID의 부모보다 나중에 만들어진 것만 자식으로 본다(PID 재사용 방지).
    // 실패한 개수를 돌려준다.
    internal static int EndTree(ProcessUsage root, IReadOnlyList<ProcessUsage> all)
    {
        var children = all.ToLookup(p => p.ParentPid);
        var visited = new HashSet<int>();
        int failures = 0;

        void Kill(ProcessUsage target)
        {
            if (target.Pid == Environment.ProcessId || !visited.Add(target.Pid))
            {
                return;
            }

            foreach (var child in children[target.Pid].Where(c => c.CreateTime >= target.CreateTime))
            {
                Kill(child);
            }

            try
            {
                EndTask(target.Key);
            }
            catch (Exception)
            {
                failures++;
            }
        }

        Kill(root);
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

    internal static void SetPriority(ProcessKey key, PriorityLevel level)
    {
        IntPtr handle = OpenVerified(key, Win32.ProcessSetInformation);
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

    internal static void SetAffinity(ProcessKey key, ulong mask)
    {
        IntPtr handle = OpenVerified(key, Win32.ProcessSetInformation | Win32.ProcessQueryInformation);
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

    // 효율 모드: 켜면 원래 우선 순위를 기억하고 낮음으로 바꾼 뒤 절전 스로틀링을 켠다.
    // 끄면 기억해 둔 우선 순위로 되돌리고(기억이 없으면 보통) 스로틀링을 푼다.
    // 순서가 중요하다: 우선 순위를 나중에 바꾸면 스로틀링 상태가 초기화되므로, 우선 순위를 먼저 바꾸고 스로틀링을 맞춘다.
    internal static void SetEfficiencyMode(ProcessKey key, bool on)
    {
        IntPtr handle = OpenVerified(key, Win32.ProcessSetInformation);
        try
        {
            uint priority;
            lock (SavedPriority)
            {
                if (on)
                {
                    uint current = Win32.GetPriorityClass(handle);
                    SavedPriority.TryAdd(key, current == 0 ? Win32.PriorityNormal : current);
                    priority = Win32.PriorityIdle;
                }
                else
                {
                    priority = SavedPriority.Remove(key, out uint saved) ? saved : Win32.PriorityNormal;
                }
            }

            if (!Win32.SetPriorityClass(handle, priority))
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
    internal static string WriteDump(ProcessKey key, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "TaskMonitorDumps");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}.{key.Pid}.dmp");

        IntPtr handle = OpenVerified(key, Win32.ProcessDumpAccess);
        try
        {
            bool ok;
            int error;
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ok = Win32.MiniDumpWriteDump(handle, (uint)key.Pid, file.SafeFileHandle.DangerousGetHandle(),
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

    // 쓰기 작업용 핸들: 열린 핸들의 생성 시각이 기록과 다르면(PID가 다른 프로그램으로 바뀜) 실패시킨다.
    // 핸들은 한 프로세스 객체에만 묶이므로, 확인한 뒤에 PID가 바뀌어도 원래 프로세스에만 작업이 간다.
    private static IntPtr OpenVerified(ProcessKey key, uint access)
    {
        IntPtr handle = OpenOrThrow(key.Pid, access | Win32.ProcessQueryLimitedInformation);
        if (!Win32.GetProcessTimes(handle, out long created, out _, out _, out _) || created != key.CreateTime)
        {
            Win32.CloseHandle(handle);
            throw new InvalidOperationException("이미 끝났거나 다른 프로세스로 바뀌었습니다.");
        }

        return handle;
    }
}
