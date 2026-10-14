using System.Diagnostics;
using System.Net.NetworkInformation;

namespace TaskMonitor;

internal sealed record PerfSample(
    double DiskBusyPercent,
    double DiskBytesPerSec,
    double NetBytesPerSec,
    double NetPercent,
    double GpuPercent,
    long TotalMemoryBytes,
    long AvailableMemoryBytes);

// 성능 페이지용 시스템 값: 디스크, 네트워크, GPU, 메모리.
// PDH 카운터 읽기는 UI 스레드에서 하면 한 번에 1초씩 멈춘다. 그래서 백그라운드 스레드가 1초마다 값을 만들고,
// UI는 가장 최근 값(Latest)만 읽는다.
internal sealed class SystemPerf : IDisposable
{
    private static readonly PerfSample Empty = new(0, 0, 0, 0, 0, 0, 0);
    private static readonly TimeSpan GpuInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GpuListInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NicListInterval = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _cts = new();
    private volatile PerfSample? _latest;

    private PerformanceCounter? _diskBusy;
    private PerformanceCounter? _diskBytes;
    private readonly Dictionary<string, PerformanceCounter> _gpu = new();
    private DateTime _gpuListAt = DateTime.MinValue;
    private DateTime _gpuReadAt = DateTime.MinValue;
    private double _gpuValue;

    private List<NetworkInterface> _interfaces = new();
    private DateTime _interfacesAt = DateTime.MinValue;
    private long _prevNetBytes = -1;
    private long _prevNetTicks;

    // 가장 최근에 측정한 값. 아직 측정 전이면 0으로 채운 값.
    public PerfSample Latest => _latest ?? Empty;

    public void Start()
    {
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    // 동기 측정(자체 검증용). 화면은 Latest를 쓴다.
    public PerfSample Sample() => Measure();

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                _latest = Measure();
            }
        }
        catch (OperationCanceledException)
        {
            // 앱 종료 신호
        }
        finally
        {
            DisposeCounters();
        }
    }

    private PerfSample Measure()
    {
        var (total, available) = Native.MemoryBytes();

        double diskBusy = ReadCounter(ref _diskBusy, "PhysicalDisk", "% Disk Time", "_Total");
        double diskBytes = ReadCounter(ref _diskBytes, "PhysicalDisk", "Disk Bytes/sec", "_Total");

        var (netBytes, netSpeed) = ReadNetwork();
        double netPerSec = 0;
        if (_prevNetBytes >= 0)
        {
            double seconds = (Environment.TickCount64 - _prevNetTicks) / 1000.0;
            if (seconds > 0)
            {
                netPerSec = Math.Max(0, netBytes - _prevNetBytes) / seconds;
            }
        }

        _prevNetBytes = netBytes;
        _prevNetTicks = Environment.TickCount64;
        double netPercent = netSpeed > 0 ? Math.Clamp(netPerSec * 8 / netSpeed * 100, 0, 100) : 0;

        return new PerfSample(
            Math.Clamp(diskBusy, 0, 100),
            diskBytes,
            netPerSec,
            netPercent,
            ReadGpu(),
            total,
            available);
    }

    // 네트워크: 작동 중인 인터페이스의 송수신 바이트 합과 링크 속도 합
    private (long Bytes, double Speed) ReadNetwork()
    {
        if (DateTime.Now - _interfacesAt > NicListInterval)
        {
            _interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();
            _interfacesAt = DateTime.Now;
        }

        long bytes = 0;
        double speed = 0;
        foreach (var ni in _interfaces)
        {
            try
            {
                var stats = ni.GetIPv4Statistics();
                bytes += stats.BytesReceived + stats.BytesSent;
                speed += ni.Speed;
            }
            catch (NetworkInformationException)
            {
                // 인터페이스가 사라졌거나 통계를 못 읽는 경우는 건너뛴다.
            }
        }

        return (bytes, speed);
    }

    // GPU: "GPU Engine" 카운터의 사용률 합(0~100으로 자름). 엔진 합계라서 근사치다.
    // 인스턴스 수가 많아서 10초마다만 읽고, 인스턴스 목록은 60초마다 다시 찾는다(CPU 비용을 줄이려고).
    private double ReadGpu()
    {
        if (DateTime.UtcNow - _gpuReadAt < GpuInterval)
        {
            return _gpuValue;
        }

        _gpuReadAt = DateTime.UtcNow;
        try
        {
            if (DateTime.UtcNow - _gpuListAt > GpuListInterval)
            {
                _gpuListAt = DateTime.UtcNow;
                var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames().ToHashSet();
                foreach (var stale in _gpu.Keys.Where(k => !names.Contains(k)).ToList())
                {
                    _gpu[stale].Dispose();
                    _gpu.Remove(stale);
                }

                foreach (var name in names.Where(n => !_gpu.ContainsKey(n)))
                {
                    _gpu[name] = new PerformanceCounter("GPU Engine", "Utilization Percentage", name);
                }
            }

            double sum = 0;
            foreach (var counter in _gpu.Values)
            {
                sum += counter.NextValue();
            }

            _gpuValue = Math.Clamp(sum, 0, 100);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _gpuValue = 0;
        }

        return _gpuValue;
    }

    private static double ReadCounter(ref PerformanceCounter? counter, string category, string name, string instance)
    {
        try
        {
            counter ??= new PerformanceCounter(category, name, instance);
            return counter.NextValue();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            counter = null;
            return 0;
        }
    }

    private void DisposeCounters()
    {
        _diskBusy?.Dispose();
        _diskBytes?.Dispose();
        foreach (var counter in _gpu.Values)
        {
            counter.Dispose();
        }

        _gpu.Clear();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
