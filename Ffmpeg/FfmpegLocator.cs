using static Cliperino.Core.Lang;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Cliperino.Core;

namespace Cliperino.Ffmpeg;

public sealed record FfmpegCapabilities(
    string Version, bool HasDdagrab, bool HasGdigrab, bool HasNvenc, bool HasAmf, bool HasQsv, bool HasX264, bool HasGfxcapture)
{
    public bool HasEncoder(string encoder) => encoder switch
    {
        "nvenc" => HasNvenc,
        "amf" => HasAmf,
        "qsv" => HasQsv,
        "x264" => HasX264,
        _ => false,
    };
}

public static class FfmpegLocator
{
    const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    static string ManagedPath => Path.Combine(AppPaths.FfmpegDir, "ffmpeg.exe");

    public static string? Find()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"), ManagedPath };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe")); }
            catch (ArgumentException) { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<string> DownloadAsync(IProgress<(string Text, int? Percent)> progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(AppPaths.FfmpegDir);
        var zipPath = Path.Combine(AppPaths.FfmpegDir, "ffmpeg.zip");

        await Downloader.DownloadAsync(DownloadUrl, zipPath, T("Descargando ffmpeg", "Downloading ffmpeg"), progress, cancellation);

        progress.Report((T("Descomprimiendo ffmpeg…", "Extracting ffmpeg…"), 100));
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(T("El zip descargado no contiene bin/ffmpeg.exe.", "The downloaded zip doesn't contain bin/ffmpeg.exe."));
            entry.ExtractToFile(ManagedPath, overwrite: true);
        }
        File.Delete(zipPath);
        return ManagedPath;
    }

    public static async Task<FfmpegCapabilities> InspectAsync(string ffmpeg)
    {
        var timeout = TimeSpan.FromSeconds(15);
        var version = await FfmpegProcess.RunAsync(ffmpeg, "-hide_banner -version", timeout);
        var encoders = await FfmpegProcess.RunAsync(ffmpeg, "-hide_banner -encoders", timeout);
        var filters = await FfmpegProcess.RunAsync(ffmpeg, "-hide_banner -filters", timeout);
        var devices = await FfmpegProcess.RunAsync(ffmpeg, "-hide_banner -devices", timeout);

        static bool Has(string listing, string name) => Regex.IsMatch(listing, $@"\s{Regex.Escape(name)}\s");

        return new FfmpegCapabilities(
            version.Output.Split('\n')[0].Trim(),
            Has(filters.Output, "ddagrab"),
            Has(devices.Output, "gdigrab"),
            Has(encoders.Output, "h264_nvenc"),
            Has(encoders.Output, "h264_amf"),
            Has(encoders.Output, "h264_qsv"),
            Has(encoders.Output, "libx264"),
            Has(filters.Output, "gfxcapture"));
    }
}

public static class FfmpegProcess
{
    public static Process Start(string ffmpeg, string args, bool redirectStdout = false)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = redirectStdout,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException(T("No se pudo iniciar ffmpeg.", "Couldn't start ffmpeg."));
        JobObject.Assign(process);
        return process;
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(
        string ffmpeg, string args, TimeSpan timeout, Func<Stream, Task>? feedStdin = null)
    {
        using var process = Start(ffmpeg, args, redirectStdout: true);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (feedStdin != null) await feedStdin(process.StandardInput.BaseStream);
        }
        catch (IOException)
        {
            // ffmpeg cerro stdin antes de tiempo
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync();
            return (-1, await stdout + await stderr + "\n[timeout]");
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}
