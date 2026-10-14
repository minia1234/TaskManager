using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskMonitor;

// 실제 시스템에서 핵심 호출을 검증한다.
// 작업은 우리가 직접 띄운 테스트 프로세스(cmd -> ping)에만 건다. 다른 프로세스는 건드리지 않는다.
internal static class SelfTest
{
    public static int Run(string outPath)
    {
        var log = new StringBuilder();
        int failures = 0;

        void Check(string name, bool ok, string detail)
        {
            log.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name} - {detail}");
            if (!ok)
            {
                failures++;
            }
        }

        var self = Process.GetCurrentProcess();
        var cache = new ProcessInfoCache();
        cache.BeginTick(10);
        var selfInfo = cache.Get(self.Id, self.StartTime.ToFileTimeUtc());
        Check("자기 프로세스 경로", selfInfo.Path.EndsWith("TaskMonitor.exe", StringComparison.OrdinalIgnoreCase), selfInfo.Path);
        Check("자기 프로세스 사용자", selfInfo.User.Length > 0, selfInfo.User);
        Check("명령줄 조회", selfInfo.CommandLine.Contains("--selftest"), selfInfo.CommandLine);
        Check("아키텍처", selfInfo.Architecture == "x64", selfInfo.Architecture);

        var sampler = new CpuSampler();
        sampler.Sample();
        Thread.Sleep(1000);
        var snap = sampler.Sample();
        Check("CPU 샘플", snap is not null, snap is null ? "null" : $"total={snap.TotalCpuPercent:F1} cores={snap.LogicalProcessors} processes={snap.ProcessCount}");
        if (snap is not null)
        {
            Check("코어별 값 개수", snap.PerCpuPercent.Length == snap.LogicalProcessors, $"{snap.PerCpuPercent.Length} == {snap.LogicalProcessors}");
            Check("자기 프로세스가 목록에 있음", snap.Processes.Any(p => p.Pid == self.Id), $"pid {self.Id}");

            // 틱 한 번의 단계별 비용을 잰다(CPU 샘플, 성능 샘플, 행 생성).
            using var benchPerf = new SystemPerf();
            benchPerf.Sample();
            var benchCache = new ProcessInfoCache();
            for (int i = 0; i < 3; i++)
            {
                var clock = Stopwatch.StartNew();
                var current = sampler.Sample();
                long tCpu = clock.ElapsedMilliseconds;
                clock.Restart();
                benchPerf.Sample();
                long tPerf = clock.ElapsedMilliseconds;
                clock.Restart();
                benchCache.BeginTick(40);
                if (current is not null)
                {
                    RowBuilders.Processes(current, benchCache, true);
                }

                long tRows = clock.ElapsedMilliseconds;
                Check($"벤치 {i + 1}", true, $"CPU 샘플 {tCpu}ms, 성능 샘플 {tPerf}ms, 행 생성 {tRows}ms");
                Thread.Sleep(1000);
            }
        }

        // 테스트 대상: cmd.exe -> ping.exe 부모/자식 쌍
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 300 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        Thread.Sleep(1500);

        var snap2 = sampler.Sample() ?? snap;
        var child = snap2?.Processes.FirstOrDefault(p => p.ParentPid == parent.Id && p.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));
        if (snap2 is null || child is null)
        {
            Check("테스트 자식 프로세스 확인", false, $"parent pid {parent.Id}");
            return Finish(outPath, log, failures);
        }

        Check("부모-자식 관계", child.ParentPid == parent.Id, $"ping {child.Pid} parent {child.ParentPid}");

        try
        {
            ProcessActions.SetPriority(child.Pid, PriorityLevel.BelowNormal);
            var pri = ProcessActions.GetPriority(child.Pid);
            Check("우선 순위 설정/조회", pri == PriorityLevel.BelowNormal, pri.ToString());
            ProcessActions.SetPriority(child.Pid, PriorityLevel.Normal);
        }
        catch (Exception ex)
        {
            Check("우선 순위 설정/조회", false, ex.Message);
        }

        try
        {
            ulong all = (1UL << Math.Min(Environment.ProcessorCount, 63)) - 1;
            ProcessActions.SetAffinity(child.Pid, 0x1);
            ulong one = ProcessActions.GetAffinity(child.Pid);
            Check("선호도 설정/조회 (CPU 0만)", one == 0x1, $"mask=0x{one:X}");
            ProcessActions.SetAffinity(child.Pid, all);
        }
        catch (Exception ex)
        {
            Check("선호도 설정/조회 (CPU 0만)", false, ex.Message);
        }

        try
        {
            ProcessActions.SetEfficiencyMode(child.Pid, true);
            bool on = ProcessActions.IsEfficiencyMode(child.Pid);
            Check("효율 모드 켜기", on, $"throttled={on}, priority={ProcessActions.GetPriority(child.Pid)}, raw={RawThrottle(child.Pid)}");
            ProcessActions.SetEfficiencyMode(child.Pid, false);
            bool off = !ProcessActions.IsEfficiencyMode(child.Pid);
            Check("효율 모드 끄기", off, $"throttled={!off}");
        }
        catch (Exception ex)
        {
            Check("효율 모드", false, ex.Message);
        }

        try
        {
            string dump = ProcessActions.WriteDump(child.Pid, child.Name);
            var size = new FileInfo(dump).Length;
            Check("덤프 파일 만들기", size > 0, $"{dump} ({size} bytes)");
            File.Delete(dump);
        }
        catch (Exception ex)
        {
            Check("덤프 파일 만들기", false, ex.Message);
        }

        Check("창이 없는 프로세스 전환", !ProcessActions.SwitchTo(child.Pid), "보이는 창이 없으면 false");

        int treeFailures = ProcessActions.EndTree(parent.Id, snap2!.Processes);
        Thread.Sleep(800);
        bool parentGone = parent.HasExited;
        Check("작업 트리 끝내기", treeFailures == 0 && parentGone, $"failures={treeFailures} parentExited={parentGone}");

        try
        {
            var services = ServiceList.Enumerate();
            Check("서비스 열거", services.Count > 0, $"{services.Count}개, 실행 중 PID 있음: {services.Count(s => s.ProcessId > 0)}개");
        }
        catch (Exception ex)
        {
            Check("서비스 열거", false, ex.Message);
        }

        try
        {
            var startup = StartupItems.Enumerate();
            Check("시작 프로그램 열거", true, $"{startup.Count}개");
        }
        catch (Exception ex)
        {
            Check("시작 프로그램 열거", false, ex.Message);
        }

        try
        {
            var sessions = Sessions.Enumerate();
            Check("사용자 세션 열거", sessions.Count > 0, string.Join(", ", sessions.Select(s => $"{s.Id}:{s.UserName}:{s.State}")));
        }
        catch (Exception ex)
        {
            Check("사용자 세션 열거", false, ex.Message);
        }

        try
        {
            using var perf = new SystemPerf();
            perf.Sample();
            Thread.Sleep(3500);
            var sample = perf.Sample();
            Check("성능 값(디스크·네트워크·GPU·메모리)", sample.TotalMemoryBytes > 0,
                $"disk={sample.DiskBusyPercent:F1}% net={sample.NetPercent:F2}% gpu={sample.GpuPercent:F1}% mem={sample.TotalMemoryBytes / 1073741824.0:F1}GB");
        }
        catch (Exception ex)
        {
            Check("성능 값", false, ex.Message);
        }

        return Finish(outPath, log, failures);
    }

    // 진단용: 스로틀링 원시 상태 (ControlMask, StateMask)
    private static string RawThrottle(int pid)
    {
        IntPtr handle = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return $"open failed ({Marshal.GetLastWin32Error()})";
        }

        try
        {
            var state = new Win32.PowerThrottlingState { Version = 1 };
            bool ok = Win32.GetProcessInformation(handle, 4, ref state, (uint)Marshal.SizeOf<Win32.PowerThrottlingState>());
            return ok ? $"control=0x{state.ControlMask:X} state=0x{state.StateMask:X}" : $"query failed ({Marshal.GetLastWin32Error()})";
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    private static int Finish(string outPath, StringBuilder log, int failures)
    {
        log.AppendLine();
        log.AppendLine(failures == 0 ? "모든 검사 통과" : $"실패 {failures}개");
        File.WriteAllText(outPath, log.ToString());
        return failures == 0 ? 0 : 1;
    }
}
