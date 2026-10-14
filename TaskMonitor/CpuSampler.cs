using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskMonitor;

internal sealed record ProcessUsage(
    int Pid,
    int ParentPid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    double DiskBytesPerSec,
    int Threads,
    int Handles,
    int SessionId,
    long CreateTime);

internal sealed record ExitedProcess(int Pid, string Name, double LastCpuPercent, DateTime ExitedAt);

// 같은 이름의 프로세스들을 합친 값 (예: ShaderCompileWorker.exe 29개)
internal sealed record ProcessGroup(string Name, int Count, double CpuPercent, long WorkingSetBytes, long PrivateBytes, double DiskBytesPerSec, int Threads);

// 전체 CPU를 세 부분으로 나눈다.
//  - TotalCpuPercent: 전체 사용률 (유휴 제외)
//  - DpcInterruptPercent: DPC + 하드웨어 인터럽트 (드라이버가 쓰는 시간, 작업 관리자의 "시스템 인터럽트")
//  - UnattributedPercent: 프로세스 어디에도 안 잡힌 나머지 커널 시간
internal sealed record Snapshot(
    double TotalCpuPercent,
    double DpcInterruptPercent,
    double UnattributedPercent,
    int LogicalProcessors,
    double[] PerCpuPercent,
    IReadOnlyList<ProcessUsage> Processes,
    IReadOnlyList<ProcessGroup> Groups,
    IReadOnlyList<ExitedProcess> RecentlyExited,
    int ProcessCount,
    int TotalThreads,
    int TotalHandles);

// 프로세스 1개의 원시 값. CPU 시간과 I/O 바이트는 누적값이다.
internal readonly record struct ProcessRaw(
    int Pid,
    int ParentPid,
    long CreateTime,
    long CpuTime,
    long IoBytes,
    long PrivateBytes,
    string Name,
    int Threads,
    int Handles,
    int SessionId,
    long WorkingSetBytes);

internal sealed class CpuSampler
{
    // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION 1개의 크기 (x64)
    private const int ProcessorPerfEntrySize = 48;
    private const double ExitedMinPercent = 1.0;
    private static readonly TimeSpan ExitedKeepTime = TimeSpan.FromSeconds(60);

    private readonly record struct PrevProcess(long CpuTime, long IoBytes, string Name, double Percent);
    private sealed record CpuTimes(long[] Capacity, long[] Idle, long[] Dpc, long[] Interrupt);

    private int _processBufferSize = 0x40000;
    private int _cpuBufferSize;
    private int _logicalProcessors;

    private CpuTimes? _prevCpu;
    private long _prevTicks;
    private Dictionary<(int Pid, long CreateTime), PrevProcess> _prevProcs = new();
    private readonly List<ExitedProcess> _exited = new();

    // 첫 호출은 기준점만 잡고 null을 돌려준다. 델타가 있어야 퍼센트를 계산할 수 있다.
    public Snapshot? Sample()
    {
        var cpu = ReadCpuTimes();
        var raws = ReadProcesses();
        long now = Stopwatch.GetTimestamp();

        if (_prevCpu is null)
        {
            Remember(cpu, raws, now);
            return null;
        }

        var prev = _prevCpu;
        double elapsed = (now - _prevTicks) / (double)Stopwatch.Frequency;
        long capacity = Sum(cpu.Capacity) - Sum(prev.Capacity);
        if (capacity <= 0 || elapsed <= 0)
        {
            Remember(cpu, raws, now);
            return null;
        }

        // 프로세스 퍼센트는 "전체 CPU 용량(모든 코어 합)" 대비로 계산한다. 그래서 모든 프로세스 합이 전체 사용률과 맞는다.
        double Pct(long delta) => 100.0 * delta / capacity;

        var perCpu = new double[cpu.Capacity.Length];
        for (int i = 0; i < perCpu.Length; i++)
        {
            long cap = cpu.Capacity[i] - prev.Capacity[i];
            long idle = cpu.Idle[i] - prev.Idle[i];
            perCpu[i] = cap > 0 ? Math.Clamp(100.0 * (cap - idle) / cap, 0, 100) : 0;
        }

        double total = Math.Clamp(Pct(capacity - (Sum(cpu.Idle) - Sum(prev.Idle))), 0, 100);
        double dpcInt = Math.Clamp(Pct((Sum(cpu.Dpc) - Sum(prev.Dpc)) + (Sum(cpu.Interrupt) - Sum(prev.Interrupt))), 0, 100);

        var current = new Dictionary<(int Pid, long CreateTime), PrevProcess>(raws.Count);
        var processes = new List<ProcessUsage>(raws.Count);
        double sumProcesses = 0;
        int totalThreads = 0, totalHandles = 0;

        foreach (var r in raws)
        {
            // PID 0(System Idle Process)의 CPU 시간은 유휴 시간이라 사용량에서 뺀다.
            if (r.Pid == 0)
            {
                continue;
            }

            totalThreads += r.Threads;
            totalHandles += r.Handles;

            var key = (r.Pid, r.CreateTime);
            double percent = 0, disk = 0;
            if (_prevProcs.TryGetValue(key, out var old))
            {
                percent = Math.Max(0, Pct(r.CpuTime - old.CpuTime));
                disk = Math.Max(0, r.IoBytes - old.IoBytes) / elapsed;
            }

            current[key] = new PrevProcess(r.CpuTime, r.IoBytes, r.Name, percent);
            sumProcesses += percent;
            processes.Add(new ProcessUsage(r.Pid, r.ParentPid, r.Name, percent, r.WorkingSetBytes, r.PrivateBytes,
                disk, r.Threads, r.Handles, r.SessionId, r.CreateTime));
        }

        // 이번 샘플에서 사라진 프로세스 중 직전에 CPU를 쓰던 것은 "최근 종료" 목록에 남긴다.
        foreach (var (key, old) in _prevProcs)
        {
            if (!current.ContainsKey(key) && old.Percent >= ExitedMinPercent)
            {
                _exited.Add(new ExitedProcess(key.Pid, old.Name, old.Percent, DateTime.Now));
            }
        }

        var nowDate = DateTime.Now;
        _exited.RemoveAll(e => nowDate - e.ExitedAt > ExitedKeepTime);

        _prevCpu = cpu;
        _prevTicks = now;
        _prevProcs = current;

        var sorted = processes
            .OrderByDescending(p => p.CpuPercent)
            .ThenByDescending(p => p.WorkingSetBytes)
            .ToList();

        var groups = sorted
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessGroup(
                g.Key,
                g.Count(),
                g.Sum(p => p.CpuPercent),
                g.Sum(p => p.WorkingSetBytes),
                g.Sum(p => p.PrivateBytes),
                g.Sum(p => p.DiskBytesPerSec),
                g.Sum(p => p.Threads)))
            .OrderByDescending(g => g.CpuPercent)
            .ToList();

        double unattributed = Math.Max(0, total - sumProcesses - dpcInt);
        return new Snapshot(total, dpcInt, unattributed, _logicalProcessors, perCpu, sorted, groups,
            _exited.ToList(), processes.Count, totalThreads, totalHandles);
    }

    private void Remember(CpuTimes cpu, List<ProcessRaw> raws, long now)
    {
        _prevCpu = cpu;
        _prevTicks = now;
        _prevProcs = raws
            .Where(r => r.Pid != 0)
            .ToDictionary(r => (r.Pid, r.CreateTime), r => new PrevProcess(r.CpuTime, r.IoBytes, r.Name, 0));
    }

    private static long Sum(long[] values)
    {
        long total = 0;
        foreach (var v in values)
        {
            total += v;
        }

        return total;
    }

    // 프로세서별 누적 시간을 읽는다.
    // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: 0 Idle, 8 Kernel, 16 User, 24 DPC, 32 Interrupt
    // KernelTime에는 유휴 시간이 포함되므로 전체 용량은 Kernel + User 합이다.
    private CpuTimes ReadCpuTimes()
    {
        if (_cpuBufferSize == 0)
        {
            _cpuBufferSize = Environment.ProcessorCount * ProcessorPerfEntrySize;
        }

        IntPtr buffer = Native.Query(Native.SystemProcessorPerformanceInformation, ref _cpuBufferSize, out int returned);
        try
        {
            int count = returned / ProcessorPerfEntrySize;
            _logicalProcessors = count;

            var capacity = new long[count];
            var idle = new long[count];
            var dpc = new long[count];
            var interrupt = new long[count];
            for (int i = 0; i < count; i++)
            {
                IntPtr p = IntPtr.Add(buffer, i * ProcessorPerfEntrySize);
                idle[i] = Marshal.ReadInt64(p, 0);
                capacity[i] = Marshal.ReadInt64(p, 8) + Marshal.ReadInt64(p, 16);
                dpc[i] = Marshal.ReadInt64(p, 24);
                interrupt[i] = Marshal.ReadInt64(p, 32);
            }

            return new CpuTimes(capacity, idle, dpc, interrupt);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // SYSTEM_PROCESS_INFORMATION (x64) 오프셋:
    //   0x00 NextEntryOffset, 0x04 NumberOfThreads, 0x20 CreateTime, 0x28 UserTime, 0x30 KernelTime,
    //   0x38 ImageName.Length, 0x40 ImageName.Buffer, 0x50 UniqueProcessId, 0x58 InheritedFromUniqueProcessId,
    //   0x60 HandleCount, 0x64 SessionId, 0x90 WorkingSetSize, 0xC8 PrivatePageCount,
    //   0xE8 ReadTransferCount, 0xF0 WriteTransferCount
    // 이름 버퍼는 같은 메모리 블록 안에 있으므로, 해제 전에 여기서 문자열까지 읽는다.
    private List<ProcessRaw> ReadProcesses()
    {
        IntPtr buffer = Native.Query(Native.SystemProcessInformation, ref _processBufferSize, out _);
        try
        {
            var result = new List<ProcessRaw>(256);
            int offset = 0;
            while (true)
            {
                IntPtr p = IntPtr.Add(buffer, offset);

                int pid = (int)Marshal.ReadInt64(p, 0x50);
                ushort nameLength = (ushort)Marshal.ReadInt16(p, 0x38);
                IntPtr nameBuffer = Marshal.ReadIntPtr(p, 0x40);
                string name = nameBuffer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(nameBuffer, nameLength / 2) ?? $"PID {pid}"
                    : pid switch
                    {
                        0 => "System Idle Process",
                        4 => "System",
                        _ => $"PID {pid}",
                    };

                result.Add(new ProcessRaw(
                    Pid: pid,
                    ParentPid: (int)Marshal.ReadInt64(p, 0x58),
                    CreateTime: Marshal.ReadInt64(p, 0x20),
                    CpuTime: Marshal.ReadInt64(p, 0x28) + Marshal.ReadInt64(p, 0x30),
                    IoBytes: Marshal.ReadInt64(p, 0xE8) + Marshal.ReadInt64(p, 0xF0),
                    PrivateBytes: Marshal.ReadInt64(p, 0xC8),
                    Name: name,
                    Threads: Marshal.ReadInt32(p, 0x04),
                    Handles: Marshal.ReadInt32(p, 0x60),
                    SessionId: Marshal.ReadInt32(p, 0x64),
                    WorkingSetBytes: Marshal.ReadInt64(p, 0x90)));

                int next = Marshal.ReadInt32(p, 0x00);
                if (next == 0)
                {
                    break;
                }

                offset += next;
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
