using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

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
                    RowBuilders.Processes(current, benchCache, true, benchPerf.Latest.GpuByPid);
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

        // 표 상호작용: 화면 없이 마우스·헤더 처리 메서드를 직접 호출한다.
        try
        {
            var page = new GridPage(RowBuilders.ProcessColumns, RowBuilders.DefaultProcessColumns) { Size = new System.Drawing.Size(900, 500) };
            var alpha = DemoRow("group:alpha", "alpha(2)", 5.0, 100L, group: true);
            alpha.Children!.Add(DemoRow("pid:1", "    alpha.exe", 3.0, 60L));
            alpha.Children.Add(DemoRow("pid:2", "    alpha.exe", 2.0, 40L));
            var beta = DemoRow("group:beta", "beta(1)", 1.0, 200L, group: true);
            beta.Children!.Add(DemoRow("pid:3", "    beta.exe", 1.0, 200L));
            page.SetRows(new List<Row> { alpha, beta });
            var grid = page.Grid;

            Check("묶음 접힌 상태 행 수", grid.RowCount == 2, $"{grid.RowCount}");
            // 프로세스 페이지는 큰 숫자 열 때문에 머리글이 64px다. 첫 행(묶음)의 이름 열을 누르면 펼쳐진다.
            int headerHeight = grid.HeaderHeightForTest;
            Invoke(grid, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 10, headerHeight + 14, 0));
            Check("묶음 펼치기", grid.RowCount == 4, $"{grid.RowCount}");
            Invoke(grid, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 10, headerHeight + 14, 0));
            Check("묶음 접기", grid.RowCount == 2, $"{grid.RowCount}");

            Invoke(page, "OnHeaderClicked", "memory");
            Check("메모리 헤더 정렬(큰 값 먼저)", grid.RowAt(0)?.Key == "group:beta", $"첫 행 {grid.RowAt(0)?.Key}");

            Invoke(grid, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 10, headerHeight + 14 + 28, 0));
            Check("행 선택", grid.SelectedIndex == 1 && page.SelectedRow?.Key == "group:alpha", $"선택 {grid.SelectedIndex}");
        }
        catch (Exception ex)
        {
            Check("표 상호작용", false, ex.Message);
        }

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
            double diskBusy = sample.Disks.Select(d => d.BusyPercent).DefaultIfEmpty(0).Max();
            double netUse = sample.Nics.Select(n => n.UtilizationPercent).DefaultIfEmpty(0).Max();
            double gpuUse = sample.Gpus.Select(g => g.Utilization).DefaultIfEmpty(0).Max();
            Check("성능 값(디스크·네트워크·GPU·메모리)", sample.Memory.Total > 0,
                $"disk={diskBusy:F1}% net={netUse:F2}% gpu={gpuUse:F1}% mem={sample.Memory.Total / 1073741824.0:F1}GB"
                + $" 디스크 {sample.Disks.Count}개, NIC {sample.Nics.Count}개, GPU {sample.Gpus.Count}개");
        }
        catch (Exception ex)
        {
            Check("성능 값", false, ex.Message);
        }

        // 아이콘: 로컬 경로(대조군)와 네트워크 경로(\\localhost 공유)에서 실행 파일 아이콘을 얻는지 확인한다.
        // UI 없이 IconCache를 직접 쓴다. 공유를 쓸 수 없으면 실패로 기록한다.
        string shareDir = @"C:\Users\Public\TaskMonitorIconTest";
        try
        {
            string localCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Check("로컬 경로 아이콘(대조군)", WaitForIcon(localCmd) is not null, localCmd);

            Directory.CreateDirectory(shareDir);
            File.Copy(localCmd, Path.Combine(shareDir, "NETCMD.EXE"), true);
            string uncCmd = @"\\localhost\Users\Public\TaskMonitorIconTest\NETCMD.EXE";
            var clock = Stopwatch.StartNew();
            var icon = WaitForIcon(uncCmd);
            Check("네트워크 경로 아이콘", icon is not null, $"{uncCmd} ({clock.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            Check("네트워크 경로 아이콘", false, ex.Message);
        }
        finally
        {
            if (Directory.Exists(shareDir))
            {
                Directory.Delete(shareDir, true);
            }
        }

        // 디스크 건강: NVMe·ATA 해석은 값을 아는 합성 데이터로 검사하고, NVMe 읽기는 이 PC의 실제 장치로 확인한다.
        try
        {
            var nvme = new byte[512];
            nvme[1] = 0x37;
            nvme[2] = 0x01;                                                     // 0x0137 = 311K = 38°C
            nvme[3] = 100;
            nvme[4] = 10;                                                       // 여분 100%, 임계 10%
            nvme[5] = 3;                                                        // 사용량 3%
            BinaryPrimitives.WriteUInt64LittleEndian(nvme.AsSpan(32), 1000);    // 읽은 데이터 1000단위 = 512,000,000바이트
            BinaryPrimitives.WriteUInt64LittleEndian(nvme.AsSpan(112), 56);     // 전원 켬 56회
            BinaryPrimitives.WriteUInt64LittleEndian(nvme.AsSpan(128), 1234);   // 사용 시간 1234시간
            var identity = new DiskIdentity("TEST", "S", "F", "NVMe");
            var parsed = DiskHealth.ParseNvme(0, identity, nvme);
            Check("NVMe 상태 로그 해석",
                parsed.TemperatureC == 38 && parsed.LifeLeftPercent == 97 && parsed.PowerOnHours == 1234
                    && parsed.PowerCycles == 56 && parsed.HostReadBytes == 512000000 && parsed.Verdict == "양호",
                $"온도 {parsed.TemperatureC}, 수명 {parsed.LifeLeftPercent}%, 시간 {parsed.PowerOnHours}, 판정 {parsed.Verdict}");

            var smart = new byte[512];
            var limits = new byte[512];
            SmartEntry(smart, 0, 5, 100, 100, 0);       // 재할당 섹터 0개
            SmartEntry(smart, 1, 194, 62, 40, 38);      // 온도 38°C
            SmartEntry(smart, 2, 9, 95, 95, 1234);      // 사용 시간 1234시간
            SmartEntry(smart, 3, 231, 96, 96, 0);       // 남은 SSD 수명 96%
            ThresholdEntry(limits, 0, 5, 10);           // 재할당 섹터 임계값 10
            var ata = DiskHealth.ParseAta(1, identity, smart, limits);
            Check("ATA SMART 해석", ata.TemperatureC == 38 && ata.LifeLeftPercent == 96 && ata.PowerOnHours == 1234 && ata.Verdict == "양호",
                $"온도 {ata.TemperatureC}, 수명 {ata.LifeLeftPercent}%, 판정 {ata.Verdict}");

            SmartEntry(smart, 0, 5, 5, 5, 0);           // 현재값 5 <= 임계 10 → 나쁨
            var bad = DiskHealth.ParseAta(1, identity, smart, limits);
            Check("ATA 임계값 초과 판정", bad.Verdict == "나쁨" && bad.Attributes.Any(a => a.Bad), $"판정 {bad.Verdict}");

            var real = DiskHealth.ReadAll();
            var nvmeReal = real.Values.Where(v => v.Identity.Bus == "NVMe").ToList();
            string summary = string.Join("; ", real.Values.Select(v =>
                $"#{v.Number} {v.Identity.Model} [{v.Identity.Bus}] {v.Verdict} 온도 {(v.TemperatureC is { } tc ? tc + "°C" : "-")}"
                + $" 수명 {(v.LifeLeftPercent is { } life ? life + "%" : "-")} 사용 {(v.PowerOnHours is { } poh ? poh + "시간" : "-")}"
                + $" 켬 {(v.PowerCycles is { } pc ? pc + "회" : "-")} 읽기 {(v.HostReadBytes is { } hr ? DiskHealth.FormatBytes(hr) : "-")}"
                + $" 쓰기 {(v.HostWriteBytes is { } hw ? DiskHealth.FormatBytes(hw) : "-")} {v.Note}"));
            Check("디스크 장치 읽기", real.Count > 0, summary);
            Check("NVMe 상태 로그 읽기(관리자 권한 없이)", nvmeReal.Count > 0 && nvmeReal.All(v => v.TemperatureC is not null), $"NVMe {nvmeReal.Count}개");
        }
        catch (Exception ex)
        {
            Check("디스크 건강", false, ex.Message);
        }

        // GPU: NVML로 NVIDIA GPU 상세 값을 읽는다(관리자 권한 없이).
        try
        {
            var gpus = Nvml.ReadAll();
            string gpuSummary = string.Join("; ", gpus.Select(g =>
                $"{g.Name} 드라이버 {g.DriverVersion} 온도 {g.TemperatureC?.ToString() ?? "-"}°C"
                + $" 전력 {(g.PowerWatts is { } w ? w.ToString("F1") + " W" : "-")}"
                + $" VRAM {(g.VramTotalBytes is { } vt ? DiskHealth.FormatBytes(vt) : "-")}"
                + $" 프로세스 {g.Processes.Count}개 클럭 제한 0x{(g.ThrottleReasons ?? -1):X} 인코딩 세션 {g.EncoderSessions.Count}개"));
            Check("NVIDIA GPU 상세 값(NVML)", gpus.Count > 0 && gpus.All(g => g.TemperatureC is not null && g.VramTotalBytes > 0), gpuSummary);
        }
        catch (Exception ex)
        {
            Check("NVIDIA GPU 상세 값(NVML)", false, ex.Message);
        }

        // 앱 기록: 경과 시간 × CPU 사용률 × 코어 수만큼 CPU 시간이 쌓이는지 확인한다(저장된 기록은 건드리지 않는다).
        try
        {
            var history = new AppHistory();
            var before = history.Snapshot().FirstOrDefault(r => r.Name == "selftest.exe")?.CpuSeconds ?? 0;
            var fake = new ProcessUsage(1, 0, "selftest.exe", 50, 0, 0, 0, 1, 1, 1, 0);
            history.Update(new[] { fake }, 4);
            Thread.Sleep(1000);
            history.Update(new[] { fake }, 4);
            double gained = (history.Snapshot().FirstOrDefault(r => r.Name == "selftest.exe")?.CpuSeconds ?? 0) - before;
            // 50% × 코어 4개 × 약 1초 = 약 2 코어·초
            Check("앱 기록 CPU 시간 누적", gained > 1.5 && gained < 2.6, $"{gained:F2} 코어·초");
        }
        catch (Exception ex)
        {
            Check("앱 기록 CPU 시간 누적", false, ex.Message);
        }

        // 언어: 영어 전환 후 메뉴 문구가 바뀌고, 한국어로 돌아오면 원래 문구가 나온다.
        Strings.Apply("en");
        bool englishOk = Strings.Localize("파일") == "File" && Strings.Localize("프로세스") == "Processes";
        Strings.Apply("ko");
        bool koreanOk = Strings.Localize("File") == "파일" && Strings.Localize("프로세스") == "프로세스";
        Check("언어 전환(한국어·영어)", englishOk && koreanOk, $"영어 {englishOk}, 한국어 복귀 {koreanOk}");

        // 현재 실행 권한: 관리자 권한 여부는 메뉴 항목과 SMART 읽기 가능 여부를 결정한다(정보용이라 항상 통과).
        Check("실행 권한", true, MainForm.IsElevated() ? "관리자 권한으로 실행 중" : "일반 권한으로 실행 중");

        // NVIDIA 핫스팟·메모리 접합부 온도(NVAPI): 핫스팟은 코어 온도 이상이어야 한다. 구조체 크기는 헤더 기준 168바이트다.
        try
        {
            Check("NVAPI 구조체 크기(168바이트)", Nvapi.ThermalSensorsSize() == 168, $"{Nvapi.ThermalSensorsSize()}바이트");
            var nv = Nvapi.ReadAll(new[] { "NVIDIA GeForce RTX 4070 SUPER" });
            var nvml = Nvml.ReadAll();
            string detail = Nvapi.Status + "; " + string.Join("; ", nv.Select(n =>
                $"#{n.Index} 핫스팟 {n.HotSpotC?.ToString() ?? "-"}°C 메모리 접합부 {n.MemoryJunctionC?.ToString() ?? "-"}°C"));
            int? core = nvml.FirstOrDefault()?.TemperatureC;
            bool ok = nv.Count > 0 && nv[0].HotSpotC is { } hot && core is { } c && hot >= c && nv[0].MemoryJunctionC is not null;
            Check("NVAPI 핫스팟·메모리 접합부 온도", ok, detail + $"; NVML 코어 {core?.ToString() ?? "-"}°C");
        }
        catch (Exception ex)
        {
            Check("NVAPI 핫스팟·메모리 접합부 온도", false, ex.Message);
        }

        // 프로세스별 VRAM: GPU Process Memory 카운터(문서화된 WDDM 카운터)
        try
        {
            using var vramPerf = new SystemPerf();
            vramPerf.Sample();
            var byLuid = vramPerf.Sample().GpuVramByLuid;
            int processCount = byLuid.Values.Sum(v => v.Count);
            long totalBytes = byLuid.Values.SelectMany(v => v).Sum(p => p.Bytes);
            Check("프로세스별 VRAM(GPU Process Memory)", processCount > 0,
                $"어댑터 {byLuid.Count}개, 프로세스 {processCount}개, 합계 {DiskHealth.FormatBytes(totalBytes)}");
        }
        catch (Exception ex)
        {
            Check("프로세스별 VRAM", false, ex.Message);
        }

        // AMD: ADL 구조체 크기는 헤더 기준(온도 8바이트, 활동 40바이트)과 같아야 한다. AMD GPU가 없으면 빈 목록이 정상이다.
        try
        {
            var (temperatureSize, activitySize) = Adl.StructSizes();
            Check("ADL 구조체 크기(헤더 기준 8/40바이트)", temperatureSize == 8 && activitySize == 40, $"온도 {temperatureSize}, 활동 {activitySize}");
            var amd = Adl.ReadAll();
            string amdValues = string.Join("; ", amd.Select(g =>
                $"{g.Name} 온도 {g.TemperatureC?.ToString() ?? "-"}°C 사용률 {g.GpuUtilPercent?.ToString() ?? "-"}%"
                + $" 그래픽 {g.GraphicsMHz?.ToString() ?? "-"} MHz 메모리 {g.MemoryMHz?.ToString() ?? "-"} MHz"
                + $" VRAM {(g.VramUsedBytes is { } v ? DiskHealth.FormatBytes(v) : "-")}"
                + $" 전력 {(g.PowerWatts is { } w ? w.ToString("F0") + " W" : "-")} PCIe x{g.PcieWidth?.ToString() ?? "-"}"));
            Check("AMD ADL 읽기(AMD 없으면 빈 목록)", true, $"{Adl.Status}, 읽은 GPU {amd.Count}개{(amdValues.Length > 0 ? ": " + amdValues : "")}");
        }
        catch (Exception ex)
        {
            Check("AMD ADL 읽기", false, ex.Message);
        }

        static void SmartEntry(byte[] page, int index, int id, int current, int worst, long raw)
        {
            int o = 2 + index * 12;
            page[o] = (byte)id;
            page[o + 3] = (byte)current;
            page[o + 4] = (byte)worst;
            for (int k = 0; k < 6; k++)
            {
                page[o + 5 + k] = (byte)(raw >> (8 * k));
            }
        }

        static void ThresholdEntry(byte[] page, int index, int id, int threshold)
        {
            int o = 2 + index * 12;
            page[o] = (byte)id;
            page[o + 1] = (byte)threshold;
        }

        return Finish(outPath, log, failures);
    }

    // IconCache는 백그라운드에서 추출하므로 준비될 때까지 잠시 기다린다(최대 8초).
    private static System.Drawing.Bitmap? WaitForIcon(string path)
    {
        var clock = Stopwatch.StartNew();
        System.Drawing.Bitmap? icon = null;
        while (icon is null && clock.ElapsedMilliseconds < 8000)
        {
            icon = IconCache.Get(path);
            if (icon is null)
            {
                Thread.Sleep(200);
            }
        }

        return icon;
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

    private static Row DemoRow(string key, string name, double cpu, long memory, bool group = false)
    {
        var row = new Row { Key = key, Children = group ? new List<Row>() : null };
        row.Cells["name"] = name;
        row.Sort["name"] = name;
        row.Cells["cpu"] = $"{cpu:F1}%";
        row.Sort["cpu"] = cpu;
        row.Cells["memory"] = $"{memory}MB";
        row.Sort["memory"] = memory;
        return row;
    }

    // 마우스·헤더 처리 메서드는 private/protected이므로 리플렉션으로 호출한다(검증 전용).
    private static void Invoke(object target, string method, params object[] args)
    {
        var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().Name, method);
        m.Invoke(target, args);
    }

    private static int Finish(string outPath, StringBuilder log, int failures)
    {
        log.AppendLine();
        log.AppendLine(failures == 0 ? "모든 검사 통과" : $"실패 {failures}개");
        File.WriteAllText(outPath, log.ToString());
        return failures == 0 ? 0 : 1;
    }
}
