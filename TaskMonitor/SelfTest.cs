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

        // PID와 생성 시각: 목록의 생성 시각과 열린 핸들의 생성 시각이 같아야 한다.
        var childKey = ProcessActions.KeyOf(child.Pid);
        Check("생성 시각 일치(PID 재사용 가드 기준)", childKey.CreateTime == child.CreateTime, $"ping {child.Pid}");

        // 다른 생성 시각(= 같은 PID의 다른 프로그램)이면 우선 순위를 바꾸지 않고 거부해야 한다.
        try
        {
            bool refused = false;
            try
            {
                ProcessActions.SetPriority(new ProcessKey(childKey.Pid, childKey.CreateTime - 1), PriorityLevel.Idle);
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            Check("PID 재사용 가드(다른 생성 시각은 거부)", refused && ProcessActions.GetPriority(child.Pid) == PriorityLevel.Normal, $"거부 {refused}");
        }
        catch (Exception ex)
        {
            Check("PID 재사용 가드(다른 생성 시각은 거부)", false, ex.Message);
        }

        try
        {
            ProcessActions.SetPriority(childKey, PriorityLevel.BelowNormal);
            var pri = ProcessActions.GetPriority(child.Pid);
            Check("우선 순위 설정/조회", pri == PriorityLevel.BelowNormal, pri.ToString());
            ProcessActions.SetPriority(childKey, PriorityLevel.Normal);
        }
        catch (Exception ex)
        {
            Check("우선 순위 설정/조회", false, ex.Message);
        }

        try
        {
            ulong all = (1UL << Math.Min(Environment.ProcessorCount, 63)) - 1;
            ProcessActions.SetAffinity(childKey, 0x1);
            ulong one = ProcessActions.GetAffinity(child.Pid);
            Check("선호도 설정/조회 (CPU 0만)", one == 0x1, $"mask=0x{one:X}");
            ProcessActions.SetAffinity(childKey, all);
        }
        catch (Exception ex)
        {
            Check("선호도 설정/조회 (CPU 0만)", false, ex.Message);
        }

        try
        {
            ProcessActions.SetEfficiencyMode(childKey, true);
            bool on = ProcessActions.IsEfficiencyMode(child.Pid);
            Check("효율 모드 켜기", on, $"throttled={on}, priority={ProcessActions.GetPriority(child.Pid)}, raw={RawThrottle(child.Pid)}");
            ProcessActions.SetEfficiencyMode(childKey, false);
            bool off = !ProcessActions.IsEfficiencyMode(child.Pid);
            Check("효율 모드 끄기", off, $"throttled={!off}");
        }
        catch (Exception ex)
        {
            Check("효율 모드", false, ex.Message);
        }

        // 효율 모드를 끄면 켜기 전 우선 순위(낮음)로 돌아가야 한다. 켜져 있는 동안은 낮음(Idle)이다.
        try
        {
            ProcessActions.SetPriority(childKey, PriorityLevel.BelowNormal);
            ProcessActions.SetEfficiencyMode(childKey, true);
            var whileOn = ProcessActions.GetPriority(child.Pid);
            ProcessActions.SetEfficiencyMode(childKey, false);
            var afterOff = ProcessActions.GetPriority(child.Pid);
            Check("효율 모드 해제 시 이전 우선 순위 복원", whileOn == PriorityLevel.Idle && afterOff == PriorityLevel.BelowNormal,
                $"켠 동안 {whileOn}, 끈 뒤 {afterOff}");
            ProcessActions.SetPriority(childKey, PriorityLevel.Normal);
        }
        catch (Exception ex)
        {
            Check("효율 모드 해제 시 이전 우선 순위 복원", false, ex.Message);
        }

        try
        {
            string dump = ProcessActions.WriteDump(childKey, child.Name);
            var size = new FileInfo(dump).Length;
            Check("덤프 파일 만들기", size > 0, $"{dump} ({size} bytes)");
            File.Delete(dump);
        }
        catch (Exception ex)
        {
            Check("덤프 파일 만들기", false, ex.Message);
        }

        Check("창이 없는 프로세스 전환", !ProcessActions.SwitchTo(child.Pid), "보이는 창이 없으면 false");

        var parentUsage = snap2!.Processes.FirstOrDefault(p => p.Pid == parent.Id);
        int treeFailures = parentUsage is null ? -1 : ProcessActions.EndTree(parentUsage, snap2.Processes);
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
            var nvml = Nvml.ReadAll();
            var nvidia = nvml.Where(d => d.Name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase)).ToList();
            var nv = Nvapi.ReadAll(nvidia);
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

        // GPU 식별: PDH 카드의 LUID를 PCI 위치로 바꾸면 NVML 장치와 맞아야 하고, NVAPI 버스 번호도 NVML과 같아야 한다.
        // NVIDIA GPU가 없으면 통과(정보용)한다.
        try
        {
            using var identityPerf = new SystemPerf();
            identityPerf.Sample();
            var cards = identityPerf.Sample().Gpus;
            var nvidia = Nvml.ReadAll().Where(d => d.Name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase)).ToList();
            var nvapi = Nvapi.ReadAll(nvidia);
            int matched = nvidia.Count(d => cards.Any(card =>
                GpuLocation.Find(card.Luid) is { } loc && d.PciBus == loc.Bus && d.PciDevice == loc.Device));
            Check("GPU 위치 일치(LUID→PCI가 NVML 장치와 맞음)", nvidia.Count == 0 || matched == nvidia.Count,
                $"NVIDIA {nvidia.Count}개 중 {matched}개 짝지음, 카드 {cards.Count}개");
            bool busAgrees = nvapi.Any(n => n.Bus is not null) && nvapi.All(n => n.Bus is null || nvidia.Any(d => d.PciBus == n.Bus));
            Check("NVAPI 버스 번호가 NVML과 일치", nvidia.Count == 0 || busAgrees,
                $"NVAPI 버스 {string.Join(", ", nvapi.Select(n => n.Bus?.ToString() ?? "-"))}, NVML 버스 {string.Join(", ", nvidia.Select(d => d.PciBus?.ToString() ?? "-"))}");
        }
        catch (Exception ex)
        {
            Check("GPU 위치 일치(LUID→PCI가 NVML 장치와 맞음)", false, ex.Message);
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

        // 장기 기록: 파일 회전, 시작·종료 추적, 경고 규칙, 자원·활동 저장, 연결 표, 프로세스 트리, 리포트, 내보내기, 분석 창.
        // 기록 파일은 임시 폴더에 만들고 끝나면 지운다(실제 기록 폴더를 건드리지 않는다).
        ProcessUsage Usage(int pid, int parentPid, string name, long createTime) =>
            new(pid, parentPid, name, 0, 0, 0, 0, 1, 1, 1, createTime);
        Snapshot Snap(double cpu) => new(cpu, 0, 0, 8, Array.Empty<double>(),
            new[] { Usage(300, 1, "hog.exe", 500) }, Array.Empty<ProcessGroup>(), Array.Empty<ExitedProcess>(), 1, 1, 1, cpu, 0);
        PerfSnapshot Perf(double ramPercent) => PerfSnapshot.Empty with
        {
            Memory = new MemoryStats(100, (long)(100 - ramPercent), 0, 0, 0, 0, 0),
        };
        GpuDetail TestGpu(int temp) => new("NVIDIA 시험", "", temp, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null, Array.Empty<GpuProcessMemory>(), null,
            Array.Empty<GpuEncoderSession>(), null, null, null, null);

        string recordDir = Path.Combine(Path.GetTempPath(), "TaskMonitorSelfTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(recordDir);

            var rotating = new JsonLinesFile(Path.Combine(recordDir, "rotate.jsonl"), 200);
            for (int i = 0; i < 10; i++)
            {
                rotating.Append(new[] { $"{{\"line\":{i},\"padding\":\"0123456789012345678901234567890123456789\"}}" });
            }

            // 파일은 현재 것과 ".1" 하나만 남는다. 그래서 오래된 줄은 밀려나고, 가장 최근 줄은 반드시 남아야 한다.
            var kept = rotating.ReadAll().ToList();
            Check("기록 파일 회전(.1 하나만 남기고 최근 줄은 보존)", File.Exists(Path.Combine(recordDir, "rotate.jsonl.1"))
                && kept.Count > 0 && kept.Count < 10 && kept[^1].Contains("\"line\":9,"),
                $"남은 줄 {kept.Count}");

            var tracker = new ProcessEventTracker();
            var stamp = DateTime.Now;
            var baseline = tracker.Update(new[] { Usage(100, 1, "alpha.exe", 1000) }, stamp);
            var started = tracker.Update(new[] { Usage(100, 1, "alpha.exe", 1000), Usage(200, 100, "beta.exe", 2000) }, stamp);
            var exited = tracker.Update(new[] { Usage(200, 100, "beta.exe", 2000) }, stamp);
            var reused = tracker.Update(new[] { Usage(200, 100, "gamma.exe", 3000) }, stamp);
            bool trackerOk = baseline.Count == 0
                && started.Count == 1 && started[0].Kind == ActivityLog.Start && started[0].Subject == "beta.exe"
                && exited.Count == 1 && exited[0].Kind == ActivityLog.Exit && exited[0].Subject == "alpha.exe"
                && reused.Count == 2;
            Check("프로세스 시작·종료 추적(PID 재사용은 종료 + 시작)", trackerOk,
                $"기준 {baseline.Count}, 시작 {started.Count}, 종료 {exited.Count}, 재사용 {reused.Count}");

            var rules = new AlertRules();
            var t0 = new DateTime(2026, 10, 16, 12, 0, 0);
            var noDisks = new Dictionary<int, DiskHealthInfo>();
            var gpu90 = new[] { TestGpu(90) };
            var gpu79 = new[] { TestGpu(79) };
            var e0 = rules.Evaluate(t0, Snap(95), Perf(95), gpu90, noDisks);
            var e1 = rules.Evaluate(t0.AddSeconds(1), Snap(95), Perf(95), gpu90, noDisks);
            var e2 = rules.Evaluate(t0.AddSeconds(21), Snap(95), Perf(95), gpu90, noDisks);
            var e3 = rules.Evaluate(t0.AddSeconds(22), Snap(95), Perf(88), new[] { TestGpu(80) }, noDisks);
            var e4 = rules.Evaluate(t0.AddSeconds(23), Snap(95), Perf(80), gpu79, noDisks);
            var e5 = rules.Evaluate(t0.AddSeconds(24), Snap(95), Perf(92), gpu90, noDisks);
            bool alertsOk = e0.Count == 2 && e0.Any(a => a.Subject == "메모리 부족") && e0.Any(a => a.Subject == "GPU 과열")
                && e1.Count == 0
                && e2.Count == 1 && e2[0].Subject == "CPU 높음 지속"
                && e3.Count == 0 && e4.Count == 0
                && e5.Count == 2;
            Check("경고 규칙(한 번만 울림, 20초 지속 CPU, 떨림 방지)", alertsOk,
                $"0초 {e0.Count}, 1초 {e1.Count}, 21초 {e2.Count}, 22초 {e3.Count}, 23초 {e4.Count}, 24초 {e5.Count}");

            var resources = new ResourceLog(Path.Combine(recordDir, "resource.jsonl"));
            resources.Add(new ResourceSample(stamp.AddSeconds(-10), 40, 50, 8L << 30, 30, 61, 2L << 30));
            resources.Add(new ResourceSample(stamp, 42, 51, 8L << 30, 35, null, null));
            var readBack = resources.ReadRange(stamp.AddMinutes(-1), stamp.AddMinutes(1));
            Check("자원 기록 저장·읽기", readBack.Count == 2 && readBack[0].GpuTempC == 61 && readBack[1].GpuTempC is null,
                $"읽은 {readBack.Count}개");

            var activity = new ActivityLog(Path.Combine(recordDir, "activity.jsonl"));
            activity.Add(new[]
            {
                new ActivityEvent(stamp, ActivityLog.Alert, "메모리 부족", "사용 95%", 0),
                new ActivityEvent(stamp, ActivityLog.Start, "beta.exe", "PID 200", 200),
            });
            var ranged = activity.ReadRange(stamp.AddMinutes(-1), stamp.AddMinutes(1));
            Check("활동 기록 저장·읽기", activity.Recent().Count == 2 && ranged.Count == 2 && ranged[0].Kind == ActivityLog.Alert,
                $"최근 {activity.Recent().Count}건, 기간 {ranged.Count}건");

            try
            {
                NetTable.Refresh();
                int connections = NetTable.Latest.Values.Sum(v => v.Count);
                Check("연결 표 읽기(iphlpapi, 소유 PID)", connections > 0, $"PID {NetTable.Latest.Count}개, 연결 {connections}개");
            }
            catch (Exception ex)
            {
                Check("연결 표 읽기(iphlpapi, 소유 PID)", false, ex.Message);
            }

            var treeSnap = new Snapshot(0, 0, 0, 8, Array.Empty<double>(),
                new[] { Usage(100, 1, "root.exe", 1000), Usage(200, 100, "child.exe", 2000), Usage(300, 999, "orphan.exe", 3000) },
                Array.Empty<ProcessGroup>(), Array.Empty<ExitedProcess>(), 3, 0, 0, 0, 0);
            var treeRows = RowBuilders.ProcessTree(treeSnap, new ProcessInfoCache(), new Dictionary<int, double>());
            var rootRow = treeRows.FirstOrDefault(r => r.Key == "pid:100");
            bool treeOk = treeRows.Count == 4 && rootRow?.Children?.Count == 1 && rootRow.Children[0].Key == "pid:200"
                && treeRows.Any(r => r.Key == "pid:300");
            Check("프로세스 트리(자식은 부모 밑, 부모 없으면 최상위)", treeOk, $"행 {treeRows.Count}개");

            var reportNow = stamp.AddSeconds(5);
            string report = CauseReport.Build(reportNow, resources, activity, Array.Empty<AppHistory.Row>());
            Check("원인 분석 리포트 생성", report.Contains("# 원인 분석 리포트") && report.Contains("| CPU |") && report.Contains("메모리 부족"),
                $"{report.Length}자");

            var exported = CauseReport.ExportCsv(resources, activity, Path.Combine(recordDir, "export"));
            string csvHead = File.ReadLines(exported.ResourcePath).First();
            Check("기록 CSV 내보내기", File.Exists(exported.ActivityPath) && csvHead.StartsWith("time,cpu_percent", StringComparison.Ordinal),
                $"{Path.GetFileName(exported.ResourcePath)}: {csvHead}");

            // 프로세스 CPU 급증: 한 프로세스가 전체 CPU 50% 이상을 20초 넘게 쓰면 한 번만 울린다.
            var hogRules = new AlertRules();
            var hogT0 = new DateTime(2026, 10, 16, 13, 0, 0);
            var hog = new ProcessUsage(400, 1, "miner.exe", 70, 0, 0, 0, 1, 1, 1, 9000);
            var hogSnap = new Snapshot(70, 0, 0, 8, Array.Empty<double>(), new[] { hog }, Array.Empty<ProcessGroup>(),
                Array.Empty<ExitedProcess>(), 1, 1, 1, 70, 0);
            var h0 = hogRules.Evaluate(hogT0, hogSnap, PerfSnapshot.Empty, Array.Empty<GpuDetail>(), noDisks);
            var h1 = hogRules.Evaluate(hogT0.AddSeconds(21), hogSnap, PerfSnapshot.Empty, Array.Empty<GpuDetail>(), noDisks);
            var h2 = hogRules.Evaluate(hogT0.AddSeconds(22), hogSnap, PerfSnapshot.Empty, Array.Empty<GpuDetail>(), noDisks);
            Check("프로세스 CPU 급증 경고(20초 지속, 한 번만)", h0.Count == 0 && h1.Count == 1 && h1[0].Subject == "프로세스 CPU 급증" && h2.Count == 0,
                $"0초 {h0.Count}, 21초 {h1.Count}, 22초 {h2.Count}");

            // 게임 세션: 전체 화면 + GPU 3D 30% 이상이 10초 이어지면 시작하고, 10초 끊기면 요약과 함께 끝난다.
            var games = new GameSessionTracker();
            var g0 = new DateTime(2026, 10, 16, 14, 0, 0);
            var gameSnap = new Snapshot(60, 0, 0, 8, Array.Empty<double>(),
                new[] { new ProcessUsage(500, 1, "game.exe", 40, 0, 0, 0, 1, 1, 1, 100) },
                Array.Empty<ProcessGroup>(), Array.Empty<ExitedProcess>(), 1, 1, 1, 60, 0);
            var gamePerf = PerfSnapshot.Empty with
            {
                Gpus = new[] { new GpuStats("GPU 0", 90, new Dictionary<string, double> { ["3D"] = 90 }, 0, 0, 0, 0, "0x0_0x1") },
            };
            var g1 = games.Update(g0, gameSnap, gamePerf, Array.Empty<GpuDetail>(), new Dictionary<int, FrameStats>(), (500, true));
            var g2 = games.Update(g0.AddSeconds(11), gameSnap, gamePerf, Array.Empty<GpuDetail>(), new Dictionary<int, FrameStats>(), (500, true));
            var g3 = games.Update(g0.AddSeconds(20), gameSnap, gamePerf, Array.Empty<GpuDetail>(), new Dictionary<int, FrameStats>(), (0, false));
            var g4 = games.Update(g0.AddSeconds(31), gameSnap, gamePerf, Array.Empty<GpuDetail>(), new Dictionary<int, FrameStats>(), (0, false));
            bool gameOk = g1.Count == 0 && g2.Count == 1 && g2[0].Kind == ActivityLog.Game && g2[0].Detail.StartsWith("시작", StringComparison.Ordinal)
                && g3.Count == 0 && g4.Count == 1 && g4[0].Detail.StartsWith("끝", StringComparison.Ordinal) && g4[0].Detail.Contains("GPU 3D 평균 90%");
            Check("게임 세션 시작·끝(전체 화면 + GPU 3D 30% 이상, 10초 확인)", gameOk,
                $"시작 {g2.Count}, 끝 {g4.Count}" + (g4.Count > 0 ? $": {g4[0].Detail}" : ""));

            // 기록 비교와 리포트의 게임 세션 절
            string comparison = RecordComparison.Build(resources, activity,
                new RecordComparison.Period(stamp.AddMinutes(-1), stamp.AddMinutes(1)),
                new RecordComparison.Period(stamp.AddHours(-3), stamp.AddHours(-2)));
            Check("기록 비교 표(평균·최고·차이, 경고 수)", comparison.Contains("CPU %") && comparison.Contains("기간 A") && comparison.Contains("경고: A"),
                $"{comparison.Length}자");
            string reportWithGames = CauseReport.Build(stamp.AddSeconds(5), resources, activity, Array.Empty<AppHistory.Row>());
            Check("원인 분석 리포트에 게임 세션 절", reportWithGames.Contains("## 게임 세션"), "섹션 있음");

            // 앱 기록: 키는 실행 파일 경로다. 이 프로세스(TaskMonitor.exe)의 행이 경로와 함께 나와야 한다.
            var pathHistory = new AppHistory();
            var selfUsage = new ProcessUsage(Environment.ProcessId, 1, "TaskMonitor.exe", 1, 0, 0, 0, 1, 1, 1, 1);
            pathHistory.Update(new[] { selfUsage }, 8);
            Thread.Sleep(300);
            pathHistory.Update(new[] { selfUsage }, 8);
            // 실제 기록 파일에는 옛 방식(이름 키) 행이 남아 있을 수 있어서, 경로가 붙은 행을 찾는다.
            var selfRow = pathHistory.Snapshot().FirstOrDefault(r => r.Path.EndsWith("TaskMonitor.exe", StringComparison.OrdinalIgnoreCase));
            Check("앱 기록: 키는 실행 파일 경로(경로 열 포함)", selfRow is not null,
                selfRow is null ? "경로가 붙은 행 없음" : selfRow.Path);

            // ETW 교정(관리자 권한 없이): 루프백 TCP 보내기·받기 각 300KB, UDP 32KB 데이터그램 세 개.
            // 이 프로세스의 누적 바이트가 기대값과 3% 안에서 맞아야 한다(TCP 600KB + UDP 96KB).
            using (var etw = new EtwMonitor())
            {
                etw.Start();
                Thread.Sleep(500);
                const int etwChunk = 100 * 1024;
                var etwListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 9131);
                etwListener.Start();
                var etwServer = new Thread(() =>
                {
                    using var peer = etwListener.AcceptTcpClient();
                    var etwBuf = new byte[65536];
                    long etwGot = 0;
                    var etwServerStream = peer.GetStream();
                    int etwR;
                    while (etwGot < 3L * etwChunk && (etwR = etwServerStream.Read(etwBuf, 0, etwBuf.Length)) > 0)
                    {
                        etwGot += etwR;
                    }

                    etwServerStream.Write(new byte[3 * etwChunk], 0, 3 * etwChunk);
                }) { IsBackground = true };
                etwServer.Start();
                using (var etwClient = new System.Net.Sockets.TcpClient("127.0.0.1", 9131))
                {
                    var etwClientStream = etwClient.GetStream();
                    var etwPayload = new byte[etwChunk];
                    for (int etwI = 0; etwI < 3; etwI++)
                    {
                        etwClientStream.Write(etwPayload, 0, etwPayload.Length);
                    }

                    long etwBack = 0;
                    var etwInbox = new byte[65536];
                    int etwM;
                    while (etwBack < 3L * etwChunk && (etwM = etwClientStream.Read(etwInbox, 0, etwInbox.Length)) > 0)
                    {
                        etwBack += etwM;
                    }
                }

                etwServer.Join(10000);
                etwListener.Stop();

                using (var etwReceiver = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9132)))
                using (var etwSender = new System.Net.Sockets.UdpClient())
                {
                    // 1200바이트 데이터그램(조각나지 않는 크기) 스무 개: 32KB처럼 조각나면 받는 쪽에서 버려질 수 있다.
                    etwReceiver.Client.ReceiveTimeout = 3000;
                    var etwDatagram = new byte[1200];
                    for (int etwI = 0; etwI < 20; etwI++)
                    {
                        etwSender.Send(etwDatagram, etwDatagram.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9132));
                    }

                    var etwRemote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                    for (int etwI = 0; etwI < 20; etwI++)
                    {
                        try
                        {
                            etwReceiver.Receive(ref etwRemote);
                        }
                        catch (System.Net.Sockets.SocketException)
                        {
                            break;
                        }
                    }
                }

                Thread.Sleep(3000);
                var etwTotals = etw.Totals().GetValueOrDefault(Environment.ProcessId) ?? new NetTotals(0, 0);
                const long etwExpected = 3L * etwChunk * 2 + 20L * 1200;
                // TCP은 정확히 맞아야 하고, UDP 데이터그램 한 개(1200바이트)가 빠져도 실패하도록 허용 오차를 0.5%로 둔다.
                bool etwSentOk = Math.Abs(etwTotals.SentBytes - etwExpected) <= etwExpected * 0.005;
                bool etwRecvOk = Math.Abs(etwTotals.ReceivedBytes - etwExpected) <= etwExpected * 0.005;
                Check("ETW 네트워크 교정(관리자 없이 TCP·UDP 바이트가 기대값과 맞음)", etw.Status == "실행 중" && etwSentOk && etwRecvOk,
                    $"상태 {etw.Status}, 보냄 {etwTotals.SentBytes} (기대 {etwExpected}), 받음 {etwTotals.ReceivedBytes} (기대 {etwExpected})");

                // 프레임 측정: 이 PC에서 프레젠트(DXGI)를 보내는 프로세스가 있는지만 본다(정보용: 없는 PC에서는 0개가 맞다).
                var frameNow = etw.Frames(DateTime.Now);
                Check("프레임 측정(DXGI 프레젠트 수신, 정보용)", true,
                    $"프레젠트 중인 프로세스 {frameNow.Count}개" + (frameNow.Count > 0 ? $", 최고 {frameNow.Values.Max(f => f.Fps):F0} FPS" : ""));
            }

            // 이벤트 페이지 안내 문구: 행이 없으면 보이고, 행이 생기면 가려진다.
            var emptyPage = new GridPage(RowBuilders.ActivityColumns, RowBuilders.ActivityColumns.Select(c => c.Key)) { EmptyText = "안내" };
            emptyPage.SetRows(new List<Row>());
            bool emptyShown = emptyPage.EmptyTextVisible;
            emptyPage.SetRows(RowBuilders.Activity(new[] { new ActivityEvent(DateTime.Now, ActivityLog.Alert, "시험", "내용", 0) }));
            bool emptyHidden = !emptyPage.EmptyTextVisible;
            Check("이벤트 안내 문구(행이 없을 때만 보임)", emptyShown && emptyHidden, $"빈 목록 {emptyShown}, 행 있음 {emptyHidden}");

            // HWiNFO 공유 메모리 해석: 합성 메모리(같은 구조)를 만들어 CPU 온도와 팬 칸을 읽는지 확인한다.
            // 실제 HWiNFO가 쓰는 메모리와의 일치는 HWiNFO를 켠 상태에서만 확인할 수 있다(정보용으로 표시).
            string sensorName = "Local\\TaskMonitorSensorTest-" + Guid.NewGuid().ToString("N");
            using (var sensorMap = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(sensorName, 4096))
            using (var sensorView = sensorMap.CreateViewAccessor(0, 4096))
            {
                const int readingSize = 316;
                sensorView.Write(0, (uint)0x53695748);
                sensorView.Write(32, (uint)64);            // 읽기 칸 시작
                sensorView.Write(36, (uint)readingSize);    // 읽기 칸 크기
                sensorView.Write(40, (uint)2);              // 읽기 칸 개수
                void WriteReading(int index, int kind, string label, string unit, double value)
                {
                    long at = 64 + (long)index * readingSize;
                    sensorView.Write(at, kind);
                    var labelBytes = System.Text.Encoding.ASCII.GetBytes(label);
                    sensorView.WriteArray(at + 12, labelBytes, 0, labelBytes.Length);
                    var unitBytes = System.Text.Encoding.ASCII.GetBytes(unit);
                    sensorView.WriteArray(at + 268, unitBytes, 0, unitBytes.Length);
                    sensorView.Write(at + 284, value);
                }

                WriteReading(0, HwinfoSensors.KindTemperature, "CPU Package", "C", 67.5);
                WriteReading(1, HwinfoSensors.KindFan, "CPU Fan", "RPM", 1200);
                var sensors = HwinfoSensors.Read(sensorName);
                double? cpuTempRead = HwinfoSensors.CpuTemperatureC(sensors);
                bool fanOk = sensors.Any(r => r.Kind == HwinfoSensors.KindFan && r.Label == "CPU Fan" && r.Value == 1200);
                Check("HWiNFO 공유 메모리 해석(CPU 온도·팬, 합성 메모리)", cpuTempRead == 67.5 && fanOk,
                    $"CPU {cpuTempRead?.ToString("F1") ?? "-"}°C, 팬 칸 {(fanOk ? "있음" : "없음")}");
            }

            var analysisSnap = sampler.Sample();
            string analysis = ProcessAnalysisText.Build(Environment.ProcessId, analysisSnap, null, activity);
            Check("프로세스 분석 창 내용(자기 자신)", analysis.Contains("TaskMonitor") && analysis.Contains("[네트워크 연결]"),
                analysis.Split('\n')[0].Trim());
        }
        catch (Exception ex)
        {
            Check("장기 기록 검사", false, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(recordDir, true);
            }
            catch (IOException)
            {
                // 임시 폴더가 잠겨 있으면 그대로 둔다.
            }
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
