using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace TaskMonitor;

// 연결 한 건. Protocol: TCP | UDP. 수신 대기나 UDP처럼 원격 주소가 없으면 Remote는 빈 문자열이다.
internal sealed record NetConnection(int Pid, string Protocol, string Local, string Remote, string State);

// 프로세스별 네트워크 연결: iphlpapi의 소유 PID 표(GetExtendedTcpTable·GetExtendedUdpTable)를 읽는다.
// 관리자 권한 없이 모든 프로세스의 연결 목록을 읽는다. 연결별 보낸·받은 바이트는 ETW나 통계 조회가 필요해서 제공하지 않는다.
internal static class NetTable
{
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;

    // 행 크기와 칸 위치는 Windows SDK의 MIB_*_OWNER_PID 구조체 배치를 따른다(모두 4바이트 정렬).
    private const int TcpRowSize = 24;
    private const int Tcp6RowSize = 56;
    private const int UdpRowSize = 12;

    private static readonly string[] TcpStates =
    {
        "", "닫힘", "듣는 중", "연결 요청 보냄", "연결 요청 받음", "연결됨",
        "종료 대기 1", "종료 대기 2", "닫기 대기", "마지막 확인", "시간 대기", "삭제 중",
    };

    private static IReadOnlyDictionary<int, IReadOnlyList<NetConnection>> _latest = new Dictionary<int, IReadOnlyList<NetConnection>>();

    private delegate uint TableFunction(IntPtr buffer, ref uint size);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool order, uint family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, bool order, uint family, int tableClass, uint reserved);

    // PID별 연결 목록(마지막 갱신 결과). 화면은 이 값만 읽는다.
    public static IReadOnlyDictionary<int, IReadOnlyList<NetConnection>> Latest => Volatile.Read(ref _latest);

    // 표 네 개(TCP IPv4·IPv6, UDP IPv4·IPv6)를 다시 읽는다. 한 표가 실패해도 나머지는 채운다.
    public static void Refresh()
    {
        var list = new List<NetConnection>();
        ReadTable((b, ref s) => GetExtendedTcpTable(b, ref s, false, AfInet, TcpTableOwnerPidAll, 0), t => ParseTcp(t, list, false));
        ReadTable((b, ref s) => GetExtendedTcpTable(b, ref s, false, AfInet6, TcpTableOwnerPidAll, 0), t => ParseTcp(t, list, true));
        ReadTable((b, ref s) => GetExtendedUdpTable(b, ref s, false, AfInet, UdpTableOwnerPid, 0), t => ParseUdp(t, list, false));
        ReadTable((b, ref s) => GetExtendedUdpTable(b, ref s, false, AfInet6, UdpTableOwnerPid, 0), t => ParseUdp(t, list, true));

        var byPid = list.GroupBy(c => c.Pid).ToDictionary(g => g.Key, g => (IReadOnlyList<NetConnection>)g.ToList());
        Volatile.Write(ref _latest, byPid);
    }

    // 표 크기를 먼저 물어보고 읽는다. 읽는 사이에 연결이 늘어 버퍼가 모자라면 다시 시도한다.
    private static void ReadTable(TableFunction read, Action<IntPtr> parse)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            read(IntPtr.Zero, ref size);
            IntPtr buffer = Marshal.AllocHGlobal((int)Math.Max(size, 4u));
            try
            {
                uint rc = read(buffer, ref size);
                if (rc == 0)
                {
                    parse(buffer);
                    return;
                }

                if (rc != ErrorInsufficientBuffer)
                {
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // MIB_TCPTABLE_OWNER_PID / MIB_TCP6TABLE_OWNER_PID: 첫 4바이트가 행 수, 행은 4바이트부터 시작한다.
    private static void ParseTcp(IntPtr table, List<NetConnection> list, bool ipv6)
    {
        int count = Marshal.ReadInt32(table);
        int rowSize = ipv6 ? Tcp6RowSize : TcpRowSize;
        for (int i = 0; i < count; i++)
        {
            IntPtr row = IntPtr.Add(table, 4 + i * rowSize);
            int state = Marshal.ReadInt32(row, ipv6 ? 48 : 0);
            int pid = Marshal.ReadInt32(row, ipv6 ? 52 : 20);
            string local = ipv6 ? Endpoint(row, 0, 20, 16) : Endpoint(row, 4, 8, 4);
            string remote = ipv6 ? Endpoint(row, 24, 44, 16) : Endpoint(row, 12, 16, 4);
            string stateText = state >= 0 && state < TcpStates.Length ? TcpStates[state] : "";
            list.Add(new NetConnection(pid, "TCP", local, remote, stateText));
        }
    }

    // MIB_UDPTABLE_OWNER_PID / MIB_UDP6TABLE_OWNER_PID: 원격 주소가 없고 상태도 없다.
    private static void ParseUdp(IntPtr table, List<NetConnection> list, bool ipv6)
    {
        int count = Marshal.ReadInt32(table);
        int rowSize = ipv6 ? 28 : UdpRowSize;
        int addressLength = ipv6 ? 16 : 4;
        for (int i = 0; i < count; i++)
        {
            IntPtr row = IntPtr.Add(table, 4 + i * rowSize);
            int pid = Marshal.ReadInt32(row, rowSize - 4);
            // 행 배치: 주소(0), 범위 ID(IPv6만), 포트, 소유 PID(마지막 4바이트)
            string local = Endpoint(row, 0, ipv6 ? 20 : 4, addressLength);
            list.Add(new NetConnection(pid, "UDP", local, "", "-"));
        }
    }

    // 주소와 포트를 "addr:port" 문자열로 만든다. 주소와 포트가 모두 0이면 빈 문자열이다.
    private static string Endpoint(IntPtr row, int addressOffset, int portOffset, int addressLength)
    {
        var bytes = new byte[addressLength];
        Marshal.Copy(IntPtr.Add(row, addressOffset), bytes, 0, addressLength);
        ushort port = BinaryPrimitives.ReverseEndianness((ushort)(Marshal.ReadInt32(row, portOffset) & 0xFFFF));
        var address = new IPAddress(bytes);
        bool unspecified = address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        if (port == 0 && unspecified)
        {
            return "";
        }

        return addressLength == 16 ? $"[{address}]:{port}" : $"{address}:{port}";
    }
}
