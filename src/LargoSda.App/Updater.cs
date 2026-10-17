using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace LargoSda.App;

public sealed record UpdateInfo(string Tag, string Url, long Size);

/// <summary>Auto-update from GitHub releases: check, download, swap the exe, restart.</summary>
public static class Updater
{
    public const string Repo = "largodevelop/largo-sda";
    public const string AssetName = "LargoSDA.exe";
    public const string ApplyFlag = "--apply-update";

    private static readonly HttpClient Api = Create(TimeSpan.FromSeconds(20));
    private static readonly HttpClient Files = Create(Timeout.InfiniteTimeSpan);

    private static HttpClient Create(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LargoSDA-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static Version Parse(string text)
    {
        text = text.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out var v) ? v : new Version(0, 0, 0);
    }

    public static bool IsNewer(string tag, string current) => Parse(tag) > Parse(current);

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Api.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!IsNewer(tag, CurrentVersion)) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            return new UpdateInfo(tag, asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64());
        }
        return null;
    }

    private static string CurrentExe => Environment.ProcessPath ?? "";

    public static string StagedPath =>
        Path.Combine(Path.GetDirectoryName(CurrentExe)!, Path.GetFileNameWithoutExtension(CurrentExe) + ".update.exe");

    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dest = StagedPath;
        var part = dest + ".part";
        using (var response = await Files.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(part);
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (total > 0) progress?.Report((double)written / total);
            }
        }
        if (info.Size > 0 && new FileInfo(part).Length != info.Size)
        {
            File.Delete(part);
            throw new InvalidOperationException("Файл обновления скачался не полностью.");
        }
        File.Move(part, dest, true);
        return dest;
    }

    /// <summary>Start the downloaded exe in swap mode. The caller must exit right after.</summary>
    public static void StageRestart(string stagedExe)
    {
        Process.Start(new ProcessStartInfo(stagedExe)
        {
            ArgumentList = { ApplyFlag, Environment.ProcessId.ToString(), CurrentExe },
            WorkingDirectory = Path.GetDirectoryName(stagedExe)!,
            UseShellExecute = false,
        });
    }

    /// <summary>Remove leftovers of a finished update next to the exe.</summary>
    public static void Cleanup()
    {
        try
        {
            if (Path.GetFileName(CurrentExe).Contains(".update", StringComparison.OrdinalIgnoreCase)) return;
            File.Delete(StagedPath);
            File.Delete(CurrentExe + ".new");
        }
        catch { /* not critical */ }
    }

    /// <summary>Swap mode: this process is the downloaded exe. Returns true if it was.</summary>
    public static bool TryApplyMode(string[] args)
    {
        var index = Array.IndexOf(args, ApplyFlag);
        if (index < 0) return false;
        if (args.Length < index + 3 || !int.TryParse(args[index + 1], out var pid)) return true;
        var target = args[index + 2];
        try
        {
            using var parent = Process.GetProcessById(pid);
            parent.WaitForExit(30_000);
        }
        catch { /* parent already gone */ }

        var me = CurrentExe;
        var temp = target + ".new";
        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                if (!string.Equals(Path.GetFullPath(me), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(me, temp, true);
                    File.Move(temp, target, true);
                }
                break;
            }
            catch (IOException) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
        }
        try { File.Delete(temp); } catch { }
        if (File.Exists(target))
            Process.Start(new ProcessStartInfo(target) { WorkingDirectory = Path.GetDirectoryName(target)!, UseShellExecute = false });
        return true;
    }
}
