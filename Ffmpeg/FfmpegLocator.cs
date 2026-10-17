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
    // el build essentials trae nvenc, amf, qsv, ddagrab y libx264
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

        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var target = File.Create(zipPath);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read, lastMark = -1;
            while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                done += read;
                int mark = total > 0 ? (int)(done * 100 / total.Value) : (int)(done >> 20);
                if (mark == lastMark) continue;
                lastMark = mark;
                progress.Report(total > 0
                    ? ($"Descargando ffmpeg… {done >> 20} de {total.Value >> 20} MB", mark)
                    : ($"Descargando ffmpeg… {mark} MB", null));
            }
        }
        catch
        {
            File.Delete(zipPath);
            throw;
        }

        progress.Report(("Descomprimiendo ffmpeg…", 100));
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("El zip descargado no contiene bin/ffmpeg.exe.");
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
        var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar ffmpeg.");
        JobObject.Assign(process);
        return process;
    }

    public static async Task<byte[]?> CaptureOutputAsync(string ffmpeg, string args, TimeSpan timeout)
    {
        using var process = Start(ffmpeg, args, redirectStdout: true);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, cts.Token);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
        await stderr;
        return process.ExitCode == 0 ? output.ToArray() : null;
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
            // ffmpeg cerro stdin antes de tiempo, el codigo de salida dice por que
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
