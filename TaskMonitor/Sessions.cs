using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TaskMonitor;

internal sealed record SessionRow(int Id, string UserName, string State, string WinStation);

// 사용자 세션 목록과 연결 끊기 / 로그오프 / 메시지 보내기 (WTS API)
internal static class Sessions
{
    private const int WtsWinStationName = 4;
    private const int WtsUserName = 5;
    private const int WtsCurrentServer = 0;
    private const uint MbOkInformation = 0x40;

    internal static string StateName(int state) => state switch
    {
        0 => "활성",
        1 => "연결됨",
        2 => "연결 중",
        3 => "섀도",
        4 => "연결 끊김",
        5 => "유휴",
        6 => "수신 대기",
        7 => "재설정",
        8 => "다운",
        9 => "초기화 중",
        _ => "알 수 없음",
    };

    internal static List<SessionRow> Enumerate()
    {
        if (!Win32.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr sessions, out uint count))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            int size = Marshal.SizeOf<Win32.WtsSessionInfo>();
            var result = new List<SessionRow>();
            for (int i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<Win32.WtsSessionInfo>(IntPtr.Add(sessions, i * size));
                string user = QueryString(s.SessionId, WtsUserName);
                string station = Marshal.PtrToStringUni(s.WinStationName) ?? "";
                result.Add(new SessionRow((int)s.SessionId, user.Length == 0 ? Strings.Localize("(시스템)") : user, StateName(s.State), station));
            }

            return result;
        }
        finally
        {
            Win32.WTSFreeMemory(sessions);
        }
    }

    internal static void Disconnect(int id)
    {
        if (!Win32.WTSDisconnectSession(IntPtr.Zero, (uint)id, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static void Logoff(int id)
    {
        if (!Win32.WTSLogoffSession(IntPtr.Zero, (uint)id, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static void SendMessage(int id, string title, string text)
    {
        // 길이는 바이트 단위(문자 수 * 2)다. 대기하지 않고 바로 돌아온다.
        if (!Win32.WTSSendMessageW(IntPtr.Zero, (uint)id, title, (uint)(title.Length * 2), text, (uint)(text.Length * 2),
                MbOkInformation, 0, out _, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static string QueryString(uint sessionId, int infoClass)
    {
        if (!Win32.WTSQuerySessionInformationW(new IntPtr(WtsCurrentServer), sessionId, infoClass, out IntPtr buffer, out _))
        {
            return "";
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Win32.WTSFreeMemory(buffer);
        }
    }
}
