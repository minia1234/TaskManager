using System.Drawing;

namespace TaskMonitor;

// 작업 대상. Members가 비어 있으면 시스템 가상 행이라 작업을 할 수 없다.
internal sealed record ProcTarget(string Name, IReadOnlyList<ProcessUsage> Members, bool IsGroup);

// 각 페이지의 표 행을 만든다. 열 값, 정렬 키, 히트맵 색, 아이콘을 여기서 정한다.
internal static class RowBuilders
{
    // 히트맵 4단계 (작업 관리자 캡처에서 샘플링한 색)
    private static Color Heat0 => Theme.Heat0;
    private static Color Heat1 => Theme.Heat1;
    private static Color Heat2 => Theme.Heat2;
    private static Color Heat3 => Theme.Heat3;

    public static readonly ColumnDef[] ProcessColumns =
    {
        new("name", "이름", 300, Fill: true),
        new("status", "상태", 110),
        new("cpu", "CPU", 80, Right: true, Big: true),
        new("memory", "메모리", 110, Right: true, Big: true),
        new("disk", "디스크", 100, Right: true, Big: true),
        new("gpu", "GPU", 80, Right: true, Big: true),
        new("pid", "PID", 70, Right: true),
        new("ppid", "부모 PID", 80, Right: true),
        new("threads", "스레드", 70, Right: true),
        new("handles", "핸들", 70, Right: true),
        new("conn", "연결", 60, Right: true),
        new("net", "네트워크", 110, Right: true),
        new("fps", "FPS", 60, Right: true),
        new("user", "사용자 이름", 170),
        new("session", "세션", 60, Right: true),
        new("arch", "아키텍처", 80),
        new("private", "개인 작업 세트", 120, Right: true),
        new("eco", "효율성 모드", 100),
        new("desc", "설명", 240),
        new("path", "경로", 320),
        new("cmd", "명령줄", 420),
    };

    public static readonly string[] DefaultProcessColumns = { "name", "status", "cpu", "memory", "disk", "gpu", "pid", "eco" };

    public static readonly ColumnDef[] DetailColumns =
    {
        new("name", "이름", 260, Fill: true),
        new("pid", "PID", 70, Right: true),
        new("status", "상태", 110),
        new("user", "사용자 이름", 170),
        new("cpu", "CPU", 70, Right: true),
        new("memory", "메모리", 100, Right: true),
        new("private", "개인 작업 세트", 120, Right: true),
        new("gpu", "GPU", 70, Right: true),
        new("threads", "스레드", 70, Right: true),
        new("arch", "아키텍처", 80),
        new("eco", "효율성 모드", 100),
        new("desc", "설명", 260),
    };

    public static readonly string[] DefaultDetailColumns = { "name", "pid", "status", "user", "cpu", "private", "gpu", "arch", "desc" };

    public static readonly ColumnDef[] HistoryColumns =
    {
        new("name", "이름", 200, Fill: true),
        new("cpu", "CPU 시간", 110, Right: true),
        new("disk", "I/O 전송량", 130, Right: true),
        new("net", "네트워크", 90, Right: true),
        new("since", "측정 시작", 140),
        new("path", "경로", 360),
    };

    public static readonly ColumnDef[] ServiceColumns =
    {
        new("name", "서비스 이름", 200),
        new("desc", "설명", 320, Fill: true),
        new("pid", "PID", 80, Right: true),
        new("status", "상태", 120),
    };

    public static readonly ColumnDef[] StartupColumns =
    {
        new("name", "이름", 240),
        new("publisher", "게시자", 200, Fill: true),
        new("status", "상태", 110),
        new("command", "명령", 420),
    };

    public static readonly ColumnDef[] UserColumns =
    {
        new("name", "사용자 이름", 200),
        new("id", "ID", 70, Right: true),
        new("status", "상태", 120),
        new("station", "세션 이름", 180, Fill: true),
    };

    public static List<Row> Processes(Snapshot s, ProcessInfoCache info, bool grouped, IReadOnlyDictionary<int, double> gpu)
    {
        var rows = new List<Row>
        {
            SystemRow("sys:dpc", "[시스템] 인터럽트 / DPC (드라이버)", "커널 가상 항목", s.DpcInterruptPercent),
            SystemRow("sys:unattributed", "[시스템] 미귀속 커널 시간", "프로세스에 안 잡힌 나머지", s.UnattributedPercent),
        };

        if (grouped)
        {
            var byName = s.Processes.ToLookup(p => p.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var g in s.Groups)
            {
                var members = byName[g.Name].ToList();
                var group = new Row
                {
                    Key = "group:" + g.Name,
                    Tag = new ProcTarget(g.Name, members, true),
                    Children = new List<Row>(),
                };
                group.Cells["name"] = $"{g.Name}({g.Count})";
                group.Sort["name"] = g.Name;
                group.Cells["status"] = "";
                group.Cells["cpu"] = FormatPercent(g.CpuPercent);
                group.Sort["cpu"] = g.CpuPercent;
                group.Heat["cpu"] = CpuHeat(g.CpuPercent);
                group.Cells["memory"] = FormatMemory(g.WorkingSetBytes);
                group.Sort["memory"] = g.WorkingSetBytes;
                group.Heat["memory"] = MemoryHeat(g.WorkingSetBytes);
                group.Cells["disk"] = FormatRate(g.DiskBytesPerSec);
                group.Sort["disk"] = g.DiskBytesPerSec;
                double groupGpu = members.Sum(m => gpu.GetValueOrDefault(m.Pid));
                group.Cells["gpu"] = FormatPercent(groupGpu);
                group.Sort["gpu"] = groupGpu;
                // 묶음 네트워크는 구성원의 합, FPS는 가장 높은 구성원 값(같은 프로그램의 여러 창이 각자 프레젠트하므로 합은 의미가 없다).
                double groupNet = members.Sum(m => EtwLatest.Rates.TryGetValue(m.Pid, out var r) ? r.SentBytesPerSec + r.ReceivedBytesPerSec : 0);
                bool anyNet = members.Any(m => EtwLatest.Rates.ContainsKey(m.Pid));
                group.Cells["net"] = anyNet ? FormatRate(groupNet) : "-";
                group.Sort["net"] = groupNet;
                double groupFps = members.Max(m => EtwLatest.Frames.TryGetValue(m.Pid, out var f) ? f.Fps : 0);
                group.Cells["fps"] = groupFps > 0 ? groupFps.ToString("F0") : "-";
                group.Sort["fps"] = groupFps;
                group.Cells["threads"] = g.Threads.ToString();
                group.Sort["threads"] = g.Threads;
                group.Cells["private"] = FormatMemory(g.PrivateBytes);
                group.Sort["private"] = g.PrivateBytes;

                foreach (var member in members)
                {
                    group.Children.Add(ProcessRow(member, info, "    ", new ProcTarget(member.Name, new[] { member }, false), gpu));
                }

                group.Icon = group.Children.Count > 0 ? group.Children[0].Icon : null;
                rows.Add(group);
            }
        }
        else
        {
            foreach (var p in s.Processes)
            {
                rows.Add(ProcessRow(p, info, "", new ProcTarget(p.Name, new[] { p }, false), gpu));
            }
        }

        foreach (var e in s.RecentlyExited)
        {
            rows.Add(ExitedRow(e));
        }

        return rows;
    }

    // 프로세스 트리: 부모가 목록에 있으면 그 밑에 붙인다. 부모가 없거나 자식이 부모보다 먼저 만들어졌으면(PID 재사용) 최상위다.
    public static List<Row> ProcessTree(Snapshot s, ProcessInfoCache info, IReadOnlyDictionary<int, double> gpu)
    {
        var rows = new List<Row>
        {
            SystemRow("sys:dpc", "[시스템] 인터럽트 / DPC (드라이버)", "커널 가상 항목", s.DpcInterruptPercent),
            SystemRow("sys:unattributed", "[시스템] 미귀속 커널 시간", "프로세스에 안 잡힌 나머지", s.UnattributedPercent),
        };

        var byPid = s.Processes.ToDictionary(p => p.Pid);
        var children = s.Processes.ToLookup(p => p.ParentPid);
        var placed = new HashSet<int>();

        Row Build(ProcessUsage p)
        {
            placed.Add(p.Pid);
            var row = ProcessRow(p, info, "", new ProcTarget(p.Name, new[] { p }, false), gpu);
            var kids = children[p.Pid]
                .Where(c => c.Pid != p.Pid && c.CreateTime >= p.CreateTime && !placed.Contains(c.Pid))
                .ToList();
            if (kids.Count > 0)
            {
                row.Children = kids.Select(Build).ToList();
            }

            return row;
        }

        foreach (var p in s.Processes)
        {
            bool root = !byPid.TryGetValue(p.ParentPid, out var parent) || p.CreateTime < parent.CreateTime;
            if (root && !placed.Contains(p.Pid))
            {
                rows.Add(Build(p));
            }
        }

        // 순환하는 부모 관계 등으로 아직 붙지 않은 프로세스는 최상위에 남겨서 빠지지 않게 한다.
        foreach (var p in s.Processes)
        {
            if (!placed.Contains(p.Pid))
            {
                rows.Add(Build(p));
            }
        }

        return rows;
    }

    public static readonly ColumnDef[] ActivityColumns =
    {
        new("time", "시각", 120),
        new("kind", "구분", 90),
        new("subject", "대상", 260, Fill: true),
        new("detail", "내용", 380),
        new("pid", "PID", 70, Right: true),
    };

    // 최신 기록이 위로 오도록 만든다. 화면 부하를 줄이려고 최근 500건만 보인다.
    public static List<Row> Activity(IReadOnlyList<ActivityEvent> events)
    {
        var rows = new List<Row>();
        for (int i = events.Count - 1; i >= 0 && rows.Count < 500; i--)
        {
            var e = events[i];
            var row = new Row { Key = $"evt:{i}:{e.Time.Ticks}:{e.Kind}:{e.Pid}" };
            row.Cells["time"] = e.Time.ToString(e.Time.Date == DateTime.Today ? "HH:mm:ss" : "MM-dd HH:mm:ss");
            row.Sort["time"] = e.Time.Ticks;
            row.Cells["kind"] = KindLabel(e.Kind);
            row.Sort["kind"] = e.Kind;
            row.Cells["subject"] = Strings.Localize(e.Subject);
            row.Sort["subject"] = e.Subject;
            row.Cells["detail"] = e.Detail;
            row.Sort["detail"] = e.Detail;
            row.Cells["pid"] = e.Pid > 0 ? e.Pid.ToString() : "-";
            row.Sort["pid"] = e.Pid;
            if (e.Kind == ActivityLog.Alert)
            {
                row.Fore = Theme.Red;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string KindLabel(string kind) => kind switch
    {
        ActivityLog.Start => Strings.Localize("시작"),
        ActivityLog.Exit => Strings.Localize("종료"),
        ActivityLog.Game => Strings.Localize("게임"),
        _ => Strings.Localize("경고"),
    };

    public static List<Row> Details(Snapshot s, ProcessInfoCache info, IReadOnlyDictionary<int, double> gpu)
    {
        var rows = new List<Row>();
        foreach (var p in s.Processes)
        {
            rows.Add(ProcessRow(p, info, "", new ProcTarget(p.Name, new[] { p }, false), gpu));
        }

        return rows;
    }

    // 앱 기록 행: CPU 시간(시:분:초), 디스크 입출력 합계, 네트워크는 제공하지 않아 "-".
    public static List<Row> History(IReadOnlyList<AppHistory.Row> apps)
    {
        var rows = new List<Row>(apps.Count);
        foreach (var app in apps)
        {
            // 같은 파일 이름이라도 경로가 다르면 다른 행이다. 경로가 없는 옛 기록은 이름으로 구분한다.
            var row = new Row { Key = app.Path.Length > 0 ? app.Path : "name:" + app.Name };
            row.Cells["name"] = app.Name;
            row.Cells["cpu"] = TimeSpan.FromSeconds(app.CpuSeconds).ToString(@"h\:mm\:ss");
            row.Cells["disk"] = DiskHealth.FormatBytes((long)app.IoBytes);
            row.Cells["net"] = "-";
            row.Cells["since"] = app.Since.ToString("yyyy-MM-dd HH:mm");
            row.Cells["path"] = app.Path;
            row.Sort["name"] = app.Name;
            row.Sort["cpu"] = app.CpuSeconds;
            row.Sort["disk"] = app.IoBytes;
            row.Sort["since"] = app.Since;
            row.Sort["path"] = app.Path;
            rows.Add(row);
        }

        return rows;
    }

    public static List<Row> Services(IEnumerable<ServiceRow> services)
    {
        return services.Select(svc =>
        {
            var row = new Row { Key = "svc:" + svc.Name, Tag = svc };
            row.Cells["name"] = svc.Name;
            row.Sort["name"] = svc.Name;
            row.Cells["desc"] = svc.DisplayName;
            row.Sort["desc"] = svc.DisplayName;
            row.Cells["pid"] = svc.ProcessId > 0 ? svc.ProcessId.ToString() : "-";
            row.Sort["pid"] = svc.ProcessId;
            row.Cells["status"] = Strings.Localize(ServiceList.StateName(svc.State));
            row.Sort["status"] = row.Cells["status"];
            return row;
        }).ToList();
    }

    public static List<Row> Startup(IEnumerable<StartupRow> items)
    {
        return items.Select(item =>
        {
            var row = new Row { Key = $"st:{item.Hive}:{item.Name}", Tag = item };
            row.Cells["name"] = item.Name;
            row.Sort["name"] = item.Name;
            row.Cells["publisher"] = item.Publisher;
            row.Sort["publisher"] = item.Publisher;
            // 버튼 문구(사용, 사용 안 함)와 상태 값은 영어에서 단어가 달라 따로 고른다.
            string status = Strings.IsEnglish ? (item.Enabled ? "Enabled" : "Disabled") : (item.Enabled ? "사용" : "사용 안 함");
            row.Cells["status"] = status;
            row.Sort["status"] = status;
            row.Cells["command"] = item.Command;
            row.Sort["command"] = item.Command;
            return row;
        }).ToList();
    }

    public static List<Row> Users(IEnumerable<SessionRow> sessions)
    {
        return sessions.Select(s =>
        {
            var row = new Row { Key = "sess:" + s.Id, Tag = s };
            row.Cells["name"] = s.UserName;
            row.Sort["name"] = s.UserName;
            row.Cells["id"] = s.Id.ToString();
            row.Sort["id"] = s.Id;
            row.Cells["status"] = Strings.Localize(s.State);
            row.Sort["status"] = row.Cells["status"];
            row.Cells["station"] = s.WinStation;
            row.Sort["station"] = s.WinStation;
            return row;
        }).ToList();
    }

    private static Row ProcessRow(ProcessUsage p, ProcessInfoCache info, string indent, ProcTarget target, IReadOnlyDictionary<int, double> gpu)
    {
        var detail = info.Get(p.Pid, p.CreateTime);
        bool eco = info.Efficiency(p.Pid);
        double gpuPercent = gpu.GetValueOrDefault(p.Pid);

        var row = new Row { Key = "pid:" + p.Pid, Tag = target, Icon = IconCache.Get(detail.Path) };
        row.Cells["name"] = indent + p.Name;
        row.Sort["name"] = p.Name;
        row.Cells["status"] = Strings.Localize("실행 중");
        row.Sort["status"] = row.Cells["status"];
        row.Cells["cpu"] = FormatPercent(p.CpuPercent);
        row.Sort["cpu"] = p.CpuPercent;
        row.Heat["cpu"] = CpuHeat(p.CpuPercent);
        row.Cells["memory"] = FormatMemory(p.WorkingSetBytes);
        row.Sort["memory"] = p.WorkingSetBytes;
        row.Heat["memory"] = MemoryHeat(p.WorkingSetBytes);
        row.Cells["disk"] = FormatRate(p.DiskBytesPerSec);
        row.Sort["disk"] = p.DiskBytesPerSec;
        row.Cells["gpu"] = FormatPercent(gpuPercent);
        row.Sort["gpu"] = gpuPercent;
        row.Cells["pid"] = p.Pid.ToString();
        row.Sort["pid"] = p.Pid;
        row.Cells["ppid"] = p.ParentPid.ToString();
        row.Sort["ppid"] = p.ParentPid;
        row.Cells["threads"] = p.Threads.ToString();
        row.Sort["threads"] = p.Threads;
        row.Cells["handles"] = p.Handles.ToString();
        row.Sort["handles"] = p.Handles;
        int connections = NetTable.Latest.TryGetValue(p.Pid, out var conns) ? conns.Count : 0;
        row.Cells["conn"] = connections.ToString();
        row.Sort["conn"] = connections;
        // 네트워크: 보내기와 받기 속도의 합(ETW). 측정이 없으면 "-".
        if (EtwLatest.Rates.TryGetValue(p.Pid, out var rate))
        {
            double bytesPerSec = rate.SentBytesPerSec + rate.ReceivedBytesPerSec;
            row.Cells["net"] = FormatRate(bytesPerSec);
            row.Sort["net"] = bytesPerSec;
        }
        else
        {
            row.Cells["net"] = "-";
            row.Sort["net"] = 0.0;
        }

        // FPS: DXGI 프레젠트 수(최근 1초). 프레젠트가 없으면 "-".
        if (EtwLatest.Frames.TryGetValue(p.Pid, out var frame))
        {
            row.Cells["fps"] = frame.Fps.ToString("F0");
            row.Sort["fps"] = frame.Fps;
        }
        else
        {
            row.Cells["fps"] = "-";
            row.Sort["fps"] = 0.0;
        }
        row.Cells["user"] = detail.User;
        row.Sort["user"] = detail.User;
        row.Cells["session"] = p.SessionId.ToString();
        row.Sort["session"] = p.SessionId;
        row.Cells["arch"] = detail.Architecture;
        row.Sort["arch"] = detail.Architecture;
        row.Cells["private"] = FormatMemory(p.PrivateBytes);
        row.Sort["private"] = p.PrivateBytes;
        row.Cells["eco"] = Strings.Localize(eco ? "예" : "아니요");
        row.Sort["eco"] = eco ? 1 : 0;
        row.Cells["desc"] = detail.Description;
        row.Sort["desc"] = detail.Description;
        row.Cells["path"] = detail.Path;
        row.Sort["path"] = detail.Path;
        row.Cells["cmd"] = detail.CommandLine;
        row.Sort["cmd"] = detail.CommandLine;
        return row;
    }

    private static Row ExitedRow(ExitedProcess e)
    {
        var row = new Row { Key = $"exit:{e.Pid}:{e.ExitedAt.Ticks}", Tag = null, Fore = Color.Gray };
        row.Cells["name"] = e.Name;
        row.Sort["name"] = e.Name;
        row.Cells["status"] = Strings.Fmt("종료됨 ({0:HH:mm:ss})", e.ExitedAt);
        row.Cells["cpu"] = FormatPercent(e.LastCpuPercent);
        row.Sort["cpu"] = e.LastCpuPercent;
        row.Cells["pid"] = e.Pid.ToString();
        row.Sort["pid"] = e.Pid;
        return row;
    }

    private static Row SystemRow(string key, string name, string status, double percent)
    {
        var row = new Row { Key = key, Tag = null };
        name = Strings.Localize(name);
        row.Cells["name"] = name;
        row.Sort["name"] = name;
        row.Cells["status"] = Strings.Localize(status);
        row.Cells["cpu"] = FormatPercent(percent);
        row.Sort["cpu"] = percent;
        row.Heat["cpu"] = CpuHeat(percent);
        row.Cells["pid"] = "-";
        return row;
    }

    public static string FormatPercent(double percent) => percent < 0.05 ? "0%" : $"{percent:F1}%";

    public static string FormatMemory(long bytes) => $"{bytes / 1048576.0:N1}MB";

    // 작업 관리자 표기: 바이트/초 단위를 KB/s, MB/s 등으로
    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond:F0} B/s";
        }

        if (bytesPerSecond < 1048576)
        {
            return $"{bytesPerSecond / 1024:F1} KB/s";
        }

        if (bytesPerSecond < 1073741824)
        {
            return $"{bytesPerSecond / 1048576:F1} MB/s";
        }

        return $"{bytesPerSecond / 1073741824:F2} GB/s";
    }

    // 단계 기준은 캡처 한 장에서 관찰한 값(0.9%→1단계 아래, 1.0%·3.2%→같은 단계)에 맞춘 근사치다.
    private static Color CpuHeat(double percent) => percent switch
    {
        < 1 => Heat0,
        < 10 => Heat1,
        < 30 => Heat2,
        _ => Heat3,
    };

    // 메모리 단계는 캡처 한 장으로 규칙을 재현하지 못해서 절대 크기 기준으로 근사했다.
    private static Color MemoryHeat(long bytes) => bytes switch
    {
        < 100L << 20 => Heat0,
        < 500L << 20 => Heat1,
        < 1L << 30 => Heat2,
        _ => Heat3,
    };
}
