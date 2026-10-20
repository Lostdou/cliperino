using static Cliperino.Core.Lang;
using System.Text;
using NAudio.Wave;
using Cliperino.Audio;
using Cliperino.Core;
using Cliperino.Ffmpeg;

namespace Cliperino.Capture;

public sealed record SavedClip(string Path, TimeSpan Duration);

public static class ClipSaver
{
    public static async Task<SavedClip> SaveAsync(
        string ffmpeg, VideoBuffer video, AudioRingBuffer? systemAudio, AudioRingBuffer? micAudio, AppSettings settings)
    {
        var endUtc = DateTime.UtcNow;
        using var pause = video.PauseCleanup();

        var segments = video.ListSegments()
            .Select(s => (s.Path, s.CreatedUtc, Pts: MpegTs.FirstPts(s.Path)))
            .Where(s => s.Pts != null)
            .Select(s => (s.Path, s.CreatedUtc, Pts: s.Pts!.Value))
            .ToList();
        if (segments.Count == 0) throw new InvalidOperationException(T("Todavía no hay nada grabado, esperá unos segundos.", "Nothing recorded yet, wait a few seconds."));

        var reference = segments[^1].Pts;
        var anchor = segments.Min(s => s.CreatedUtc - MpegTs.Elapsed(reference, s.Pts));
        DateTime ClockOf(long pts) => anchor + MpegTs.Elapsed(reference, pts);

        var target = endUtc.AddSeconds(-settings.ReplaySeconds);
        int first = segments.Count - 1;
        while (first > 0 && ClockOf(segments[first].Pts) > target) first--;

        var chosen = segments.Skip(first)
            .Select(s => (s.Path, Length: new FileInfo(s.Path).Length / MpegTs.PacketSize * MpegTs.PacketSize))
            .ToList();
        var lastPts = MpegTs.LastPts(chosen[^1].Path, chosen[^1].Length) ?? reference;

        var offset = TimeSpan.FromMilliseconds(settings.AudioOffsetMs);
        var startUtc = ClockOf(segments[first].Pts) - offset;
        var finishUtc = ClockOf(lastPts) + TimeSpan.FromSeconds(1.0 / settings.Fps) - offset;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        Directory.CreateDirectory(settings.OutputFolder);
        Directory.CreateDirectory(AppPaths.Temp);
        var output = UniquePath(Path.Combine(settings.OutputFolder, $"Cliperino_{stamp}.mp4"));

        var wavs = new List<string>();
        try
        {
            foreach (var ring in new[] { systemAudio, micAudio })
            {
                if (ring == null) continue;
                var wav = Path.Combine(AppPaths.Temp, $"{stamp}_{wavs.Count}.wav");
                var pcm = ring.Extract(startUtc, finishUtc);
                using (var writer = new WaveFileWriter(wav, ring.Format)) writer.Write(pcm, 0, pcm.Length);
                wavs.Add(wav);
            }

            var args = new StringBuilder("-hide_banner -loglevel error -y -f mpegts -i pipe:0 ");
            foreach (var wav in wavs) args.Append($"-i \"{wav}\" ");
            args.Append("-map 0:v -c:v copy ");
            if (wavs.Count == 1) args.Append("-map 1:a ");
            if (wavs.Count == 2) args.Append("-filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[a]\" -map \"[a]\" ");
            if (wavs.Count > 0) args.Append("-c:a aac -b:a 192k -ac 2 -shortest ");
            args.Append($"-movflags +faststart \"{output}\"");

            var (code, log) = await FfmpegProcess.RunAsync(ffmpeg, args.ToString(), TimeSpan.FromMinutes(5), async stdin =>
            {
                var buffer = new byte[1 << 20];
                foreach (var (path, length) in chosen)
                {
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    for (long left = length; left > 0;)
                    {
                        int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
                        if (read == 0) break;
                        await stdin.WriteAsync(buffer.AsMemory(0, read));
                        left -= read;
                    }
                }
            });

            if (code != 0 || !File.Exists(output))
            {
                Log.Write($"Falló el armado del clip (código {code}): {log}");
                var reason = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
                throw new InvalidOperationException(T($"ffmpeg no pudo armar el clip. {reason}", $"ffmpeg couldn't build the clip. {reason}"));
            }
            Log.Write($"Clip guardado: {output} ({chosen.Count} segmentos)");
            return new SavedClip(output, finishUtc - startUtc);
        }
        finally
        {
            foreach (var wav in wavs)
            {
                try { File.Delete(wav); } catch (IOException) { }
            }
        }
    }

    internal static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name}_{i}{ext}");
        return path;
    }
}
