using System.Collections.Concurrent;
using System.Drawing;

namespace TaskMonitor;

// 프로세스 실행 파일의 16px 아이콘 캐시.
// 추출은 전용 스레드가 맡는다. 네트워크 경로처럼 느린 파일이 목록 갱신을 막지 않도록, 준비되기 전에는 null을 돌려준다.
// 아이콘이 준비되면 다음 갱신 때 행에 나타난다.
internal static class IconCache
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Ready = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly HashSet<string> Queued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AutoResetEvent Signal = new(false);
    private static readonly object Gate = new();
    private static Thread? _worker;

    public static Bitmap? Get(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        if (Ready.TryGetValue(path, out var bitmap))
        {
            return bitmap;
        }

        Enqueue(path);
        return null;
    }

    private static void Enqueue(string path)
    {
        lock (Gate)
        {
            if (!Queued.Add(path))
            {
                return;
            }

            Pending.Enqueue(path);
            _worker ??= StartWorker();
        }

        Signal.Set();
    }

    private static Thread StartWorker()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "TaskMonitor icons" };
        thread.Start();
        return thread;
    }

    private static void Run()
    {
        while (true)
        {
            Signal.WaitOne();
            while (Pending.TryDequeue(out var path))
            {
                Ready[path] = Extract(path);
            }
        }
    }

    private static Bitmap? Extract(string path)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            using var small = new Icon(icon, 16, 16);
            return small.ToBitmap();
        }
        catch (Exception)
        {
            // 아이콘이 없는 실행 파일(또는 접근 불가)은 아이콘 없이 표시한다.
            return null;
        }
    }
}
