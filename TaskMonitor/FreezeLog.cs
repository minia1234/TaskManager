namespace TaskMonitor;

// 멈춤 기록: %LOCALAPPDATA%\TaskMonitor\freeze.log 에 시각과 내용을 덧붙인다.
// 감시 스레드·작업 스레드·UI 스레드가 모두 쓰므로 잠금으로 줄을 섞지 않게 한다.
internal static class FreezeLog
{
    internal static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMonitor", "freeze.log");

    private static readonly object Gate = new();

    internal static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 기록 실패는 화면 동작을 막지 않는다.
        }
    }
}
