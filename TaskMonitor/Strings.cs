namespace TaskMonitor;

// 화면 문구 번역. 기준 문구는 한국어이고, 영어 사전에 없는 문구는 한국어 그대로 보인다.
// 언어 설정은 다음 실행부터 적용된다(컨트롤을 만들 때 문구를 고르기 때문).
internal static class Strings
{
    private static readonly Dictionary<string, string> English = new()
    {
        ["파일"] = "File",
        ["새 작업 실행..."] = "Run new task...",
        ["종료"] = "Exit",
        ["옵션"] = "Options",
        ["항상 위에 표시"] = "Always on top",
        ["사용하지 않을 때 최소화"] = "Minimize when not in use",
        ["최소화 시 숨기기"] = "Hide when minimized",
        ["업데이트 속도"] = "Update speed",
        ["0.5 초"] = "0.5 s",
        ["1 초"] = "1 s",
        ["4 초"] = "4 s",
        ["일시 중지"] = "Paused",
        ["테마"] = "Theme",
        ["밝게"] = "Light",
        ["어둡게"] = "Dark",
        ["시스템 설정 따름"] = "Follow system",
        ["언어"] = "Language",
        ["한국어"] = "Korean",
        ["English"] = "English",
        ["언어는 다시 시작하면 적용됩니다."] = "The language will change after restarting.",
        ["관리자 권한으로 다시 실행"] = "Run as administrator",
        ["관리자 권한으로 실행 중"] = "Running as administrator",
        ["보기"] = "View",
        ["새로 고침"] = "Refresh",
        ["열 선택..."] = "Choose columns...",
        ["모두 펼치기"] = "Expand all",
        ["모두 접기"] = "Collapse all",
        ["이름별 합산"] = "Group by name",
        ["도움말"] = "Help",
        ["정보"] = "About",
        ["프로세스"] = "Processes",
        ["성능"] = "Performance",
        ["앱 기록"] = "App history",
        ["시작 프로그램"] = "Startup apps",
        ["사용자"] = "Users",
        ["세부 정보"] = "Details",
        ["서비스"] = "Services",
        ["작업 관리자"] = "Task Manager",
        ["검색"] = "Search",
        ["갱신 주기"] = "Update every",
        ["기록 임계치(%)"] = "Log threshold (%)",
        ["임계치 이상이면 기록"] = "Log when above threshold",
        ["작업 끝내기"] = "End task",
        ["작업 트리 끝내기"] = "End process tree",
        ["효율성 모드"] = "Efficiency mode",
        ["새 작업 실행"] = "Run new task",
        ["창으로 전환"] = "Switch to",
        ["우선 순위 설정"] = "Set priority",
        ["선호도 설정..."] = "Set affinity...",
        ["덤프 파일 만들기"] = "Create dump file",
        ["파일 위치 열기"] = "Open file location",
        ["온라인 검색"] = "Search online",
        ["속성"] = "Properties",
        ["세부 정보로 이동"] = "Go to details",
        ["시작"] = "Start",
        ["중지"] = "Stop",
        ["다시 시작"] = "Restart",
        ["서비스 열기"] = "Open Services",
        ["사용"] = "Enable",
        ["사용 안 함"] = "Disable",
        ["연결 끊기"] = "Disconnect",
        ["로그오프"] = "Sign out",
        ["메시지 보내기"] = "Send message",
        ["기록 지우기"] = "Clear history",
        ["앱 기록을 모두 지울까요?"] = "Clear all app history?",
        ["확인"] = "OK",
        ["취소"] = "Cancel",
        ["이름"] = "Name",
        ["상태"] = "Status",
        ["메모리"] = "Memory",
        ["디스크"] = "Disk",
        ["PID"] = "PID",
        ["부모 PID"] = "Parent PID",
        ["스레드"] = "Threads",
        ["핸들"] = "Handles",
        ["사용자 이름"] = "User name",
        ["세션"] = "Session",
        ["아키텍처"] = "Architecture",
        ["개인 작업 세트"] = "Private working set",
        ["설명"] = "Description",
        ["경로"] = "Path",
        ["명령줄"] = "Command line",
        ["서비스 이름"] = "Service name",
        ["게시자"] = "Publisher",
        ["명령"] = "Command",
        ["ID"] = "ID",
        ["세션 이름"] = "Session name",
        ["CPU 시간"] = "CPU time",
        ["I/O 전송량"] = "I/O transfer",
        ["이벤트"] = "Events",
        ["시각"] = "Time",
        ["구분"] = "Kind",
        ["대상"] = "Subject",
        ["내용"] = "Content",
        ["연결"] = "Connections",
        ["경고"] = "Alert",
        ["프로세스 트리로 보기"] = "Show process tree",
        ["기록"] = "Records",
        ["원인 분석 리포트 만들기 (최근 1시간)"] = "Create cause report (last hour)",
        ["기록 내보내기 (CSV)..."] = "Export records (CSV)...",
        ["기록 폴더 열기"] = "Open records folder",
        ["분석 창 열기"] = "Open analysis window",
        ["경고만 보기"] = "Alerts only",
        ["모두 보기"] = "Show all",
        ["기록 비교..."] = "Compare records...",
        ["기록 비교"] = "Compare records",
        ["비교"] = "Compare",
        ["기간 A 시작"] = "Period A start",
        ["기간 B 시작"] = "Period B start",
        ["게임"] = "Game",
        ["네트워크"] = "Network",
        ["측정 시작"] = "Since",
        ["마지막 갱신 {0}   |   프로세스 {1}개   |   기록 파일: {2}"] = "Last update {0}   |   {1} processes   |   Log file: {2}",
    };

    private static readonly Dictionary<string, string> Korean = English.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static bool IsEnglish { get; private set; }

    public static void Apply(string language)
    {
        IsEnglish = language == "en";
    }

    // 사전에 있는 문구만 바꾼다. 그 밖의 문구는 그대로 둔다.
    public static string Localize(string text)
    {
        if (IsEnglish)
        {
            return English.TryGetValue(text, out var en) ? en : text;
        }

        return Korean.TryGetValue(text, out var ko) ? ko : text;
    }

    // 한국어 기준 서식 문자열({0}, {1}...)을 현재 언어로 바꾼 뒤 채운다.
    public static string Fmt(string koreanTemplate, params object?[] args) =>
        string.Format(Localize(koreanTemplate), args);
}
