using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TaskMonitor;

// GitHub Release로 자동 업데이트한다. 최신 Release 태그를 현재 버전과 비교하고, 새 버전이면 TaskMonitor.exe를 받아
// GitHub가 알려 주는 SHA-256과 맞는지 확인한 뒤 실행 중인 exe를 .old로 비키고 새 파일을 놓는다(다음 실행부터 새 버전).
// 해시는 전송 중 손상만 막는다. 저장소 계정이 털리면 악성 파일도 해시가 맞으므로, 코드 서명을 하게 되면 게시자 확인을 더한다.
internal static class Updater
{
    public const string ReleasesPage = "https://github.com/minia1234/TaskManager/releases/latest";
    private const string LatestApi = "https://api.github.com/repos/minia1234/TaskManager/releases/latest";
    private const string AssetName = "TaskMonitor.exe";

    public enum Outcome { UpToDate, Staged, Manual, Failed }

    // Manual: 새 버전은 있지만 이 폴더에 쓸 수 없거나, 해시가 없거나, 개발 빌드라 직접 받아야 한다.
    public sealed record Result(Outcome Outcome, Version? Latest = null, string? Detail = null);

    private static readonly HttpClient Http = CreateClient();

    public static Version Current => ParseVersion(System.Windows.Forms.Application.ProductVersion) ?? new Version(0, 0);

    // 자체 검증이 가짜 Release를 가리키게 할 때만 쓴다.
    private static string ApiUrl => Environment.GetEnvironmentVariable("TASKMONITOR_UPDATE_API") ?? LatestApi;

    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var v) ? v : null;
    }

    // 지난 실행이 남긴 .old를 지운다. 이전 프로세스가 아직 끝나는 중이면 다음 실행에 다시 시도한다.
    public static void CleanupOld()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null && File.Exists(exe + ".old"))
            {
                File.Delete(exe + ".old");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static async Task<Result> CheckAsync(string? exePath = null, CancellationToken cancel = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(ApiUrl, cancel));
            var root = doc.RootElement;
            var latest = ParseVersion(root.GetProperty("tag_name").GetString());
            if (latest is null || latest <= Current)
            {
                return new Result(Outcome.UpToDate, latest);
            }

            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
            string? url = asset.ValueKind == JsonValueKind.Object ? asset.GetProperty("browser_download_url").GetString() : null;
            string? digest = asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (url is null || digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                // 해시를 모르면 받은 파일을 확인할 수 없으므로 자동으로 바꾸지 않는다.
                return new Result(Outcome.Manual, latest, "no-digest");
            }

            exePath ??= Environment.ProcessPath!;
            // 개발 빌드(옆에 .dll이 있는 exe)는 단일 파일로 바꿔 끼우면 깨진다.
            if (File.Exists(Path.ChangeExtension(exePath, ".dll")))
            {
                return new Result(Outcome.Manual, latest, "dev-build");
            }

            return await StageAsync(exePath, url, digest["sha256:".Length..], latest, cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new Result(Outcome.Failed, null, ex.Message);
        }
    }

    private static async Task<Result> StageAsync(string exePath, string url, string sha256, Version latest, CancellationToken cancel)
    {
        string fresh = exePath + ".new";
        try
        {
            await using (var output = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var input = await Http.GetStreamAsync(url, cancel))
            {
                await input.CopyToAsync(output, cancel);
            }

            string actual;
            await using (var check = File.OpenRead(fresh))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancel));
            }

            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new Result(Outcome.Failed, latest, "hash-mismatch");
            }

            // 실행 중인 exe는 지울 수 없지만 이름은 바꿀 수 있다. 새 파일을 못 놓으면 원래 이름으로 되돌린다.
            File.Move(exePath, exePath + ".old", overwrite: true);
            try
            {
                File.Move(fresh, exePath);
            }
            catch
            {
                File.Move(exePath + ".old", exePath);
                throw;
            }

            return new Result(Outcome.Staged, latest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Program Files처럼 쓸 수 없는 폴더다. Release 페이지에서 직접 받게 한다.
            return new Result(Outcome.Manual, latest, ex.Message);
        }
        finally
        {
            // 받다 끊겼거나 해시가 틀린 파일을 남기지 않는다(옮겼으면 이미 없다).
            TryDelete(fresh);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TaskMonitor/{Current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
