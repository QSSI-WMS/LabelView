using System.Diagnostics;
using System.Text.Json;

namespace LabelView;

/// <summary>
/// Checks an S3-hosted manifest for a newer version and applies the update in-place.
/// The running exe is renamed to .old (Windows allows this), the new one takes
/// its name, and the app restarts. The .old file is cleaned up on next launch.
///
/// The release assets live in a public-read S3 bucket under an unguessable key
/// prefix, so no credential is embedded in the client. The publish step uploads
/// two exes plus a <c>latest.json</c> manifest describing them.
/// </summary>
internal static class AutoUpdater
{
    /// <summary>
    /// Base URL of the release folder in the S3 bucket. Must end with a slash.
    /// Keep this in sync with the publish script's upload target. If the bucket
    /// name, region, or key prefix changes, update this one line and rebuild.
    /// </summary>
    private const string BaseUrl =
        "https://qssi-tools-autoupdate.s3.us-east-2.amazonaws.com/labelview/8a87673bba6101b9009c654b/";

    private const string ManifestUrl = BaseUrl + "latest.json";

    /// <summary>Threshold to distinguish the small (framework-dependent) exe from the full one.</summary>
    private const long FullExeThreshold = 20_000_000;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "LabelView-AutoUpdater" } }
    };

    public sealed record UpdateInfo(Version Version, string DownloadUrl, long Size);

    /// <summary>
    /// Check for a newer release. Returns null if the current version is up to date,
    /// if the network is unavailable, or if anything goes wrong.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            // Cache-buster so a stale intermediary proxy can't hide a fresh manifest.
            var json = await Http.GetStringAsync(
                ManifestUrl + "?t=" + DateTime.UtcNow.Ticks);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("version").GetString();
            if (tag == null) return null;

            var remote = Version.Parse(tag.TrimStart('v'));
            var local = typeof(AutoUpdater).Assembly.GetName().Version;
            if (local != null && remote <= local) return null;

            // Pick the right asset: match the current exe size to full vs small.
            var exePath = Environment.ProcessPath;
            bool wantFull = exePath != null && new FileInfo(exePath).Length > FullExeThreshold;
            string targetName = wantFull ? "LabelView-full.exe" : "LabelView.exe";

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    var url = asset.GetProperty("url").GetString();
                    var size = asset.GetProperty("size").GetInt64();
                    if (url != null)
                        return new UpdateInfo(remote, url, size);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Download the update and swap the exe in-place. Returns true if the caller
    /// should exit so the new version can start.
    /// </summary>
    public static async Task<bool> ApplyAsync(UpdateInfo update, IWin32Window? owner)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;

        var updatePath = exePath + ".update";
        var oldPath = exePath + ".old";

        try
        {
            // Download the new exe straight from S3 (public object, no auth).
            using var response = await Http.GetAsync(update.DownloadUrl);
            response.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(updatePath, FileMode.Create, FileAccess.Write))
                await response.Content.CopyToAsync(fs);

            // Swap: rename running → .old, rename download → original name.
            if (File.Exists(oldPath))
                try { File.Delete(oldPath); } catch { }

            File.Move(exePath, oldPath);
            File.Move(updatePath, exePath);

            // Launch the new version and signal the caller to exit.
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            // Roll back if the swap failed partway.
            try
            {
                if (!File.Exists(exePath) && File.Exists(oldPath))
                    File.Move(oldPath, exePath);
                if (File.Exists(updatePath))
                    File.Delete(updatePath);
            }
            catch { }

            MessageBox.Show(owner,
                $"Update failed: {ex.Message}",
                "LabelView", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    /// <summary>Delete the .old exe left behind by a previous update.</summary>
    public static void Cleanup()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        try { File.Delete(exe + ".old"); } catch { }
    }
}
