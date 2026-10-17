using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cliperino.Core;

namespace Cliperino.Update;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string PageUrl);

public static class Updater
{
    const string LatestReleaseUrl = "https://api.github.com/repos/Lostdou/cliperino/releases/latest";
    const string AssetName = "Cliperino.exe";
    const string AfterUpdateArgument = "--after-update";
    const string OldSuffix = ".old";
    const string DownloadSuffix = ".download";
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan PreviousInstanceTimeout = TimeSpan.FromSeconds(20);

    sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("assets")] List<Asset>? Assets);

    sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);

    public static Version CurrentVersion { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));

    public static bool CanSelfUpdate =>
        Environment.ProcessPath != null && !File.Exists(Path.Combine(AppContext.BaseDirectory, "Cliperino.dll"));

    public static bool JustUpdated { get; private set; }

    public static void FinishPendingUpdate(string[] args)
    {
        int index = Array.IndexOf(args, AfterUpdateArgument);
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var previousId))
        {
            JustUpdated = true;
            WaitForExit(previousId);
        }
        if (Environment.ProcessPath is { } path) TryDelete(path + OldSuffix);
    }

    public static async Task<UpdateInfo?> CheckAsync(Version current)
    {
        using var http = Downloader.CreateClient(RequestTimeout);
        await using var stream = await http.GetStreamAsync(LatestReleaseUrl);
        var release = await JsonSerializer.DeserializeAsync<Release>(stream);
        if (release == null || !TryParseTag(release.TagName, out var version) || version <= current) return null;
        var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        return asset == null ? null : new UpdateInfo(version, release.TagName, asset.DownloadUrl, release.HtmlUrl);
    }

    public static async Task InstallAndRestartAsync(
        UpdateInfo update, IProgress<(string Text, int? Percent)> progress, CancellationToken cancellation)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("No se encontró el ejecutable actual.");
        var downloaded = current + DownloadSuffix;
        var old = current + OldSuffix;
        await Downloader.DownloadAsync(update.DownloadUrl, downloaded, $"Descargando {update.Tag}", progress, cancellation);

        TryDelete(old);
        File.Move(current, old);
        try
        {
            File.Move(downloaded, current);
        }
        catch
        {
            File.Move(old, current);
            TryDelete(downloaded);
            throw;
        }
        Process.Start(new ProcessStartInfo(current, $"{AfterUpdateArgument} {Environment.ProcessId}") { UseShellExecute = false });
    }

    static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    static void WaitForExit(int processId)
    {
        try
        {
            using var previous = Process.GetProcessById(processId);
            previous.WaitForExit(PreviousInstanceTimeout);
        }
        catch (ArgumentException) { }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
