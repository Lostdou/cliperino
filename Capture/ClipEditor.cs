using System.Globalization;
using Cliperino.Ffmpeg;

namespace Cliperino.Capture;

public sealed record ClipEdit(double Start, double End, bool RemoveAudio, bool Exact, bool Replace);

public static class ClipEditor
{
    const double MinimumDuration = 0.1;
    const int ReplaceAttempts = 10;
    static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(15);
    static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(300);

    public static async Task<string> ExportAsync(string ffmpeg, string input, ClipEdit edit, string exactEncoderArgs)
    {
        var duration = edit.End - edit.Start;
        if (duration < MinimumDuration) throw new InvalidOperationException("El recorte es demasiado corto.");

        var directory = Path.GetDirectoryName(input)!;
        var name = Path.GetFileNameWithoutExtension(input);
        var target = edit.Replace
            ? Path.Combine(directory, $"{name}.editando.mp4")
            : ClipSaver.UniquePath(Path.Combine(directory, $"{name}_editado.mp4"));
        var video = edit.Exact ? exactEncoderArgs : "-c:v copy";
        var audio = edit.RemoveAudio ? "-an" : edit.Exact ? "-c:a aac -b:a 192k" : "-c:a copy";
        var args = $"-hide_banner -loglevel error -nostdin -y -ss {Seconds(edit.Start)} -i \"{input}\" -t {Seconds(duration)} " +
                   $"-map 0:v -map 0:a? {video} {audio} -avoid_negative_ts make_zero -movflags +faststart \"{target}\"";

        var (code, log) = await FfmpegProcess.RunAsync(ffmpeg, args, ExportTimeout);
        if (code != 0 || !File.Exists(target))
        {
            TryDelete(target);
            var reason = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            throw new InvalidOperationException(reason ?? $"ffmpeg terminó con código {code}.");
        }
        if (!edit.Replace) return target;

        try
        {
            await ReplaceAsync(target, input);
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        return input;
    }

    static async Task ReplaceAsync(string source, string destination)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReplaceAttempts)
            {
                await Task.Delay(ReplaceRetryDelay);
            }
        }
    }

    static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
