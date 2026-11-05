namespace TaskMonitor;

// 게임 세션 분석: 앞 창이 주 화면 전체를 덮고 GPU 3D 엔진 사용률이 30% 이상인 상태가 10초 이어지면 세션을 시작한다.
// 그 상태가 10초 동안 끊기면 세션을 끝내고, CPU·GPU·온도·VRAM·클럭 제한 요약을 활동 기록에 남긴다.
// 프레임 속도와 프레임 시간은 측정하지 않는다. 그 값은 PresentMon 같은 ETW 기반 도구가 필요하다.
internal sealed class GameSessionTracker
{
    private static readonly TimeSpan Confirm = TimeSpan.FromSeconds(10);
    private const double GpuLimit = 30;

    private sealed class Session
    {
        public required string Name { get; init; }
        public required int Pid { get; init; }
        public required DateTime Start { get; init; }
        public int Samples;
        public double CpuSum;
        public double CpuMax;
        public double GpuSum;
        public double GpuMax;
        public int? TempMax;
        public long VramMax;
        public int ThrottledSamples;
        public int FrameSamples;
        public double FpsSum;
        public double LowFpsSum;
    }

    private Session? _current;
    private DateTime? _candidateSince;
    private DateTime? _leftSince;

    // 샘플링 때마다 호출한다. 세션이 시작되거나 끝날 때만 활동 기록 항목을 돌려준다.
    // foreground: 앞 창의 프로세스 ID와 전체 화면 여부(Win32.ForegroundWindowState). 자체 검증에서 직접 넣을 수 있게 밖에서 받는다.
    // frames: 프로세스별 프레임 통계(EtwMonitor). 프레젠트를 못 읽은 프로세스는 FPS를 적지 않는다.
    public IReadOnlyList<ActivityEvent> Update(DateTime now, Snapshot snapshot, PerfSnapshot perf, IReadOnlyList<GpuDetail> gpus,
        IReadOnlyDictionary<int, FrameStats> frames, (int ProcessId, bool Fullscreen) foreground)
    {
        var events = new List<ActivityEvent>();
        var (pid, fullscreen) = foreground;
        var usage = pid > 0 ? snapshot.Processes.FirstOrDefault(p => p.Pid == pid) : null;
        double gpu3d = perf.Gpus.Count > 0 ? perf.Gpus.Max(g => g.Engines.GetValueOrDefault("3D")) : 0;
        bool candidate = fullscreen && usage is not null && pid != Environment.ProcessId
            && !usage.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && gpu3d >= GpuLimit;

        if (_current is null)
        {
            if (!candidate || usage is null)
            {
                _candidateSince = null;
                return events;
            }

            _candidateSince ??= now;
            if (now - _candidateSince.Value < Confirm)
            {
                return events;
            }

            _current = new Session { Name = usage.Name, Pid = pid, Start = now };
            _candidateSince = null;
            events.Add(new ActivityEvent(now, ActivityLog.Game, usage.Name, Strings.Fmt("시작 (PID {0})", pid), pid));
        }

        var session = _current;
        Accumulate(session, snapshot, gpu3d, gpus, frames.GetValueOrDefault(session.Pid));
        if (candidate && pid == session.Pid)
        {
            _leftSince = null;
            return events;
        }

        _leftSince ??= now;
        if (now - _leftSince.Value < Confirm)
        {
            return events;
        }

        events.Add(new ActivityEvent(now, ActivityLog.Game, session.Name, Summary(session, now), session.Pid));
        _current = null;
        _leftSince = null;
        return events;
    }

    private static void Accumulate(Session s, Snapshot snapshot, double gpu3d, IReadOnlyList<GpuDetail> gpus, FrameStats? frame)
    {
        s.Samples++;
        if (frame is not null)
        {
            s.FrameSamples++;
            s.FpsSum += frame.Fps;
            s.LowFpsSum += frame.OnePercentLowFps;
        }

        s.CpuSum += snapshot.TotalCpuPercent;
        s.CpuMax = Math.Max(s.CpuMax, snapshot.TotalCpuPercent);
        s.GpuSum += gpu3d;
        s.GpuMax = Math.Max(s.GpuMax, gpu3d);
        if (gpus.Select(g => g.TemperatureC).Max() is int temp)
        {
            s.TempMax = Math.Max(s.TempMax ?? temp, temp);
        }

        s.VramMax = Math.Max(s.VramMax, gpus.Sum(g => g.VramUsedBytes ?? 0));
        // NVML 비트 0x1(GPU 유휴)과 0x2(응용 프로그램 클럭 설정)는 성능을 깎는 제한이 아니므로 빼고 센다.
        if (gpus.Any(g => ((g.ThrottleReasons ?? 0) & ~0x3L) != 0))
        {
            s.ThrottledSamples++;
        }
    }

    private static string Summary(Session s, DateTime end)
    {
        double n = Math.Max(1, s.Samples);
        string temp = s.TempMax is { } t ? $"{t}°C" : "-";
        string fps = s.FrameSamples > 0
            ? Strings.Fmt("FPS 평균 {0:F0}·1% 하위 {1:F0}, ", s.FpsSum / s.FrameSamples, s.LowFpsSum / s.FrameSamples)
            : Strings.Localize("FPS 측정 없음, ");
        return Strings.Fmt("끝 (PID {0}) {1}, {2}CPU 평균 {3:F0}%·최고 {4:F0}%, ", s.Pid, FormatSpan(end - s.Start), fps, s.CpuSum / n, s.CpuMax)
            + Strings.Fmt("GPU 3D 평균 {0:F0}%·최고 {1:F0}%, GPU 온도 최고 {2}, ", s.GpuSum / n, s.GpuMax, temp)
            + Strings.Fmt("VRAM 최고 {0:F1}GB, 클럭 제한 {1}회(샘플 {2}개)", s.VramMax / 1073741824.0, s.ThrottledSamples, s.Samples);
    }

    // 끝난 세션 기록인지 본다. 기록 파일은 쓴 시점의 언어로 남으므로 한국어와 영어 시작 글자를 모두 인정한다.
    internal static bool IsEnded(ActivityEvent e) =>
        e.Kind == ActivityLog.Game
        && (e.Detail.StartsWith("끝", StringComparison.Ordinal) || e.Detail.StartsWith("Ended", StringComparison.Ordinal));

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? Strings.Fmt("{0}시간 {1}분", (int)span.TotalHours, span.Minutes) : Strings.Fmt("{0}분 {1}초", span.Minutes, span.Seconds);
}
