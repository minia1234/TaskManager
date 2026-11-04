namespace TaskMonitor;

// 멈춤 기록: %LOCALAPPDATA%\TaskMonitor\freeze.log 에 시각과 내용을 덧붙인다.
// 감시 스레드·작업 스레드·UI 스레드가 모두 쓰므로 잠금으로 줄을 섞지 않게 한다.
internal static class FreezeLog
{
    internal static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMonitor", "freeze.log");

    // 처리되지 않은 예외의 스택은 crash.log에 따로 남긴다(사용자가 그대로 보내 주면 원인을 찾을 수 있게).
    internal static readonly string CrashPath = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(FilePath)!, "crash.log");

    private static readonly object Gate = new();

    internal static void Write(string text) => Append(FilePath, text);

    internal static void Crash(Exception? ex) =>
        Append(CrashPath, $"버전 {System.Windows.Forms.Application.ProductVersion}, {Environment.OSVersion}{Environment.NewLine}{ex}");

    private static void Append(string path, string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 기록 실패는 화면 동작을 막지 않는다.
        }
    }
}
