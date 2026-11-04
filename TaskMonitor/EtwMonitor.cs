using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace TaskMonitor;

// 프로세스별 프레임 통계(DXGI 프레젠트 시각으로 계산).
// Fps: 최근 1초 안의 프레젠트 수. FrameMsAvg: 최근 2초 프레임 간격 평균. OnePercentLowFps: 최근 2초 프레임 간격의 상위 1%(가장 느린 1%)로 본 FPS.
internal sealed record FrameStats(double Fps, double FrameMsAvg, double OnePercentLowFps);

// 프로세스별 네트워크 누적 바이트(시작 이후). 초당 속도는 NetRate가 만든다.
internal sealed record NetTotals(long SentBytes, long ReceivedBytes);

internal sealed record NetRate(double SentBytesPerSec, double ReceivedBytesPerSec);

// 화면이 쓰는 최신 값. 매 샘플링마다 EtwMonitor가 채운다(RowBuilders와 분석 창이 읽는다).
internal static class EtwLatest
{
    private static IReadOnlyDictionary<int, FrameStats> _frames = new Dictionary<int, FrameStats>();
    private static IReadOnlyDictionary<int, NetRate> _rates = new Dictionary<int, NetRate>();
    private static IReadOnlyDictionary<int, NetTotals> _totals = new Dictionary<int, NetTotals>();
    private static string _status = "시작 전";

    public static IReadOnlyDictionary<int, FrameStats> Frames => Volatile.Read(ref _frames);
    public static IReadOnlyDictionary<int, NetRate> Rates => Volatile.Read(ref _rates);
    public static IReadOnlyDictionary<int, NetTotals> Totals => Volatile.Read(ref _totals);

    // ETW 세션 상태(실행 중, 또는 열지 못한 이유). 측정이 비어 있을 때 이유를 보여 주려고 함께 싣는다.
    public static string Status => Volatile.Read(ref _status);

    public static void Publish(IReadOnlyDictionary<int, FrameStats> frames, IReadOnlyDictionary<int, NetRate> rates,
        IReadOnlyDictionary<int, NetTotals> totals, string status)
    {
        Volatile.Write(ref _frames, frames);
        Volatile.Write(ref _rates, rates);
        Volatile.Write(ref _totals, totals);
        Volatile.Write(ref _status, status);
    }
}

// ETW(이벤트 추적) 실시간 세션 하나로 세 제공자를 받는다.
//  - Microsoft-Windows-DXGI: Present/Start(ID 42)의 프로세스 ID와 시각 → 프레임 통계.
//  - Microsoft-Windows-Winsock-AFD: 보내기 Enter(ID 1003, EnterExit=0)의 BufferLength → TCP 보낸 바이트,
//      받기 Exit(ID 1004, EnterExit=1)의 BufferLength → TCP 받은 바이트.
//  - Microsoft-Windows-TCPIP: 1169(UDP 보내기 NumBytes), 1170(UDP 받기 NumBytes).
// 값은 같은 PC에서 루프백 교정 시험으로 확인했다(TCP 보내기 두 번 세어지는 문제는 위치별 최댓값으로 바로잡았다).
// Performance Log Users 소속 일반 계정으로 열린다. 세션을 열지 못하면 Status에 이유가 남고 값은 비어 있다.
internal sealed class EtwMonitor : IDisposable
{
    private const int DxgiPresentStart = 42;
    private const int AfdSend = 1003;
    private const int AfdReceive = 1004;
    private const int TcpipUdpSend = 1169;
    private const int TcpipUdpRecv = 1170;
    private static readonly TimeSpan FrameWindow = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    // 프레젠트: 이벤트 시각(프레임 간격 계산용)과 도착 시각(최근 구간 고르기용). 이벤트 시각은 도착보다 약 2초 늦게 전달되므로 둘을 섞지 않는다.
    private readonly Dictionary<int, List<(DateTime Event, DateTime Arrival)>> _presents = new();
    private readonly Dictionary<(int Pid, string Location), long> _tcpSendByLocation = new();
    private readonly Dictionary<int, long> _tcpReceived = new();
    private readonly Dictionary<int, long> _udpSent = new();
    private readonly Dictionary<int, long> _udpReceived = new();
    private readonly Dictionary<int, (long Sent, long Received, DateTime At)> _lastNet = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private int _started;

    public string Status { get; private set; } = "시작 전";

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        try
        {
            // 고정 이름: 비정상 종료로 남은 세션을 다음 실행이 이어받아 정리한다(PID를 붙이면 재부팅 전까지 쌓인다).
            // 한 번에 한 인스턴스만 실행되므로(Program) 이름이 겹칠 다른 소유자는 없다.
            var session = new TraceEventSession("TaskMonitor-Etw") { StopOnDispose = true };
            _session = session;
            // 쓰는 이벤트 ID만 켠다. 제공자 전체를 켜면 프레임·패킷마다 수십 개가 와서 파싱 비용이 CPU의 대부분을 차지한다.
            Enable(session, "Microsoft-Windows-DXGI", DxgiPresentStart);
            Enable(session, "Microsoft-Windows-Winsock-AFD", AfdSend, AfdReceive);
            Enable(session, "Microsoft-Windows-TCPIP", TcpipUdpSend, TcpipUdpRecv);
            session.Source.Dynamic.All += Handle;
            _thread = new Thread(() =>
            {
                try
                {
                    session.Source.Process();
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        Status = "ETW 처리 중단: " + ex.Message;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "TaskMonitor ETW",
            };
            _thread.Start();
            Status = "실행 중";
        }
        catch (Exception ex)
        {
            Status = "ETW 세션을 열지 못함: " + ex.Message;
        }
    }

    private static void Enable(TraceEventSession session, string provider, params int[] eventIds) =>
        session.EnableProvider(provider, TraceEventLevel.Verbose, ulong.MaxValue,
            new TraceEventProviderOptions { EventIDsToEnable = eventIds.ToList() });

    private void Handle(TraceEvent d)
    {
        lock (_gate)
        {
            try
            {
                if (d.ProviderName == "Microsoft-Windows-DXGI")
                {
                    if ((int)d.ID == DxgiPresentStart)
                    {
                        AddPresent(d.ProcessID, d.TimeStamp);
                    }

                    return;
                }

                if (d.ProviderName == "Microsoft-Windows-Winsock-AFD")
                {
                    HandleAfd(d);
                    return;
                }

                if (d.ProviderName == "Microsoft-Windows-TCPIP")
                {
                    int id = (int)d.ID;
                    if (id == TcpipUdpSend || id == TcpipUdpRecv)
                    {
                        long bytes = Convert.ToInt64(d.PayloadByName("NumBytes"));
                        var table = id == TcpipUdpSend ? _udpSent : _udpReceived;
                        table[d.ProcessID] = table.GetValueOrDefault(d.ProcessID) + bytes;
                    }
                }
            }
            catch (Exception)
            {
                // 필드가 없는 이벤트 하나 때문에 이벤트 처리 전체가 멈추지 않게 한다.
            }
        }
    }

    private void HandleAfd(TraceEvent d)
    {
        int id = (int)d.ID;
        string enterExit = d.PayloadStringByName("EnterExit");
        if (id == AfdSend && enterExit == "0")
        {
            // 같은 보내기가 두 위치에서 한 번씩 기록된다. 위치별로 나눠 모으고 나중에 최댓값을 쓴다.
            long bytes = Convert.ToInt64(d.PayloadByName("BufferLength"));
            var key = (d.ProcessID, d.PayloadStringByName("Location"));
            _tcpSendByLocation[key] = _tcpSendByLocation.GetValueOrDefault(key) + bytes;
        }
        else if (id == AfdReceive && enterExit == "1")
        {
            long bytes = Convert.ToInt64(d.PayloadByName("BufferLength"));
            _tcpReceived[d.ProcessID] = _tcpReceived.GetValueOrDefault(d.ProcessID) + bytes;
        }
    }

    private void AddPresent(int pid, DateTime eventTime)
    {
        if (!_presents.TryGetValue(pid, out var list))
        {
            list = new List<(DateTime Event, DateTime Arrival)>();
            _presents[pid] = list;
        }

        var arrival = DateTime.Now;
        list.Add((eventTime, arrival));
        // 도착 기준 2초보다 오래된 프레젠트는 버린다(목록이 길어지지 않게).
        int drop = 0;
        while (drop < list.Count && arrival - list[drop].Arrival > FrameWindow)
        {
            drop++;
        }

        if (drop > 0)
        {
            list.RemoveRange(0, drop);
        }
    }

    // 프로세스별 프레임 통계. 도착 기준 최근 2초 안에 프레젠트가 2번 이상 있는 프로세스만 낸다.
    public IReadOnlyDictionary<int, FrameStats> Frames(DateTime now)
    {
        var result = new Dictionary<int, FrameStats>();
        lock (_gate)
        {
            foreach (var (pid, list) in _presents)
            {
                var recent = list.Where(p => now - p.Arrival <= FrameWindow).Select(p => p.Event).OrderBy(t => t).ToList();
                if (recent.Count < 2)
                {
                    continue;
                }

                var deltas = new List<double>(recent.Count - 1);
                for (int i = 1; i < recent.Count; i++)
                {
                    deltas.Add((recent[i] - recent[i - 1]).TotalMilliseconds);
                }

                double fps = list.Count(p => now - p.Arrival <= TimeSpan.FromSeconds(1));
                double avgMs = deltas.Average();
                deltas.Sort();
                double slowMs = deltas[(int)Math.Min(deltas.Count - 1, Math.Floor(deltas.Count * 0.99))];
                result[pid] = new FrameStats(fps, avgMs, slowMs > 0 ? 1000.0 / slowMs : 0);
            }
        }

        return result;
    }

    // 프로세스별 네트워크 누적 바이트(TCP 보내기는 위치별 최댓값, UDP는 합계).
    public IReadOnlyDictionary<int, NetTotals> Totals()
    {
        var result = new Dictionary<int, NetTotals>();
        lock (_gate)
        {
            var tcpSent = _tcpSendByLocation
                .GroupBy(kv => kv.Key.Pid)
                .ToDictionary(g => g.Key, g => g.Max(kv => kv.Value));
            var pids = tcpSent.Keys.Concat(_tcpReceived.Keys).Concat(_udpSent.Keys).Concat(_udpReceived.Keys).ToHashSet();
            foreach (int pid in pids)
            {
                long sent = tcpSent.GetValueOrDefault(pid) + _udpSent.GetValueOrDefault(pid);
                long received = _tcpReceived.GetValueOrDefault(pid) + _udpReceived.GetValueOrDefault(pid);
                result[pid] = new NetTotals(sent, received);
            }
        }

        return result;
    }

    // 지난 호출 이후의 초당 속도. 처음 본 프로세스는 속도가 0이다.
    public IReadOnlyDictionary<int, NetRate> Rates(DateTime now)
    {
        var totals = Totals();
        var result = new Dictionary<int, NetRate>();
        foreach (var (pid, total) in totals)
        {
            if (_lastNet.TryGetValue(pid, out var last))
            {
                double seconds = (now - last.At).TotalSeconds;
                if (seconds > 0)
                {
                    result[pid] = new NetRate(
                        Math.Max(0, (total.SentBytes - last.Sent) / seconds),
                        Math.Max(0, (total.ReceivedBytes - last.Received) / seconds));
                }
            }

            _lastNet[pid] = (total.SentBytes, total.ReceivedBytes, now);
        }

        return result;
    }

    // 끝난 프로세스의 값을 지운다. 표가 끝없이 커지지 않고, 같은 PID를 새로 받은 프로세스가 이전 누적을 물려받지 않는다.
    public void Prune(IReadOnlyCollection<int> alive)
    {
        lock (_gate)
        {
            foreach (int pid in _presents.Keys.Where(p => !alive.Contains(p)).ToList())
            {
                _presents.Remove(pid);
            }

            foreach (var key in _tcpSendByLocation.Keys.Where(k => !alive.Contains(k.Pid)).ToList())
            {
                _tcpSendByLocation.Remove(key);
            }

            foreach (var table in new[] { _tcpReceived, _udpSent, _udpReceived })
            {
                foreach (int pid in table.Keys.Where(p => !alive.Contains(p)).ToList())
                {
                    table.Remove(pid);
                }
            }

            foreach (int pid in _lastNet.Keys.Where(p => !alive.Contains(p)).ToList())
            {
                _lastNet.Remove(pid);
            }
        }
    }

    public void Dispose()
    {
        var thread = _thread;
        _session?.Dispose();
        thread?.Join(2000);
    }
}
