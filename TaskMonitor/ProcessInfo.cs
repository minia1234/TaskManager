using System.Diagnostics;

namespace TaskMonitor;

// 프로세스 상세 정보(경로, 사용자, 설명, 명령줄, 아키텍처).
// 조회 비용이 있어서 (PID, 생성 시각) 기준으로 캐시하고, 틱마다 조회 개수를 제한한다.
internal sealed class ProcessInfoCache
{
    internal sealed record Info(string Path, string User, string Description, string Architecture, string CommandLine);

    internal static readonly Info Empty = new("", "", "", "-", "");

    private static readonly TimeSpan EfficiencyTtl = TimeSpan.FromSeconds(5);

    private readonly Dictionary<(int Pid, long CreateTime), Info> _cache = new();
    private readonly Dictionary<int, (bool On, DateTime At)> _efficiency = new();
    private int _budget;

    // 한 틱에 새로 조회할 최대 개수. 프로세스가 많을 때 화면이 멈추지 않도록 나눠서 조회한다.
    public void BeginTick(int budget) => _budget = budget;

    // 효율 모드는 프로세스마다 핸들을 열어야 해서 5초 동안 캐시한다(틱마다 500번 열면 느리다).
    public bool Efficiency(int pid)
    {
        if (_efficiency.TryGetValue(pid, out var cached) && DateTime.Now - cached.At < EfficiencyTtl)
        {
            return cached.On;
        }

        bool on = ProcessActions.IsEfficiencyMode(pid);
        _efficiency[pid] = (on, DateTime.Now);
        return on;
    }

    public Info Get(int pid, long createTime)
    {
        if (_cache.TryGetValue((pid, createTime), out var info))
        {
            return info;
        }

        if (_budget <= 0)
        {
            return Empty;
        }

        _budget--;
        info = Query(pid);
        _cache[(pid, createTime)] = info;
        return info;
    }

    public void Prune(IEnumerable<(int Pid, long CreateTime)> alive)
    {
        var keep = new HashSet<(int Pid, long CreateTime)>(alive);
        foreach (var key in _cache.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _cache.Remove(key);
        }

        var livePids = keep.Select(k => k.Pid).ToHashSet();
        foreach (var pid in _efficiency.Keys.Where(p => !livePids.Contains(p)).ToList())
        {
            _efficiency.Remove(pid);
        }
    }

    // 실행 파일 경로만 필요할 때 쓴다(캐시와 조회 개수 제한을 거치지 않는다). 실패하면 빈 문자열.
    internal static string PathOf(int pid)
    {
        IntPtr handle = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            return Win32.QueryFullImagePath(handle);
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    private static Info Query(int pid)
    {
        IntPtr handle = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return Empty;
        }

        try
        {
            string path = Win32.QueryFullImagePath(handle);
            string user = Win32.QueryUserName(handle);
            string arch = Win32.IsWow64Process(handle, out bool wow64) ? (wow64 ? "x86" : "x64") : "-";
            string cmd = Win32.QueryCommandLine(handle);
            return new Info(path, user, DescriptionOf(path), arch, cmd);
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    private static string DescriptionOf(string path)
    {
        if (path.Length == 0)
        {
            return "";
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
