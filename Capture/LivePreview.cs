using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Cliperino.Ffmpeg;

namespace Cliperino.Capture;

public sealed class LivePreview : IDisposable
{
    public const int Width = 640;
    public const int Height = 360;
    const int FramesPerSecond = 15;
    const int FrameBytes = Width * Height * 3;
    const int ChunkSize = 1 << 16;
    static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(30);
    static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan CaptureRetryDelay = TimeSpan.FromSeconds(2);

    static readonly string Fit =
        $"scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2,format=bgr24";
    const string Output = "-f rawvideo -flush_packets 1 pipe:1";

    readonly string ffmpeg;
    readonly string arguments;
    readonly Func<IReadOnlyList<Segment>>? segments;
    readonly CancellationTokenSource stop = new();

    public event Action<Bitmap>? FrameReady;

    LivePreview(string ffmpeg, string arguments, Func<IReadOnlyList<Segment>>? segments)
    {
        this.ffmpeg = ffmpeg;
        this.arguments = arguments;
        this.segments = segments;
    }

    public static LivePreview FromBuffer(string ffmpeg, Func<IReadOnlyList<Segment>> segments) => new(ffmpeg,
        "-hide_banner -loglevel error -fflags nobuffer -flags low_delay -probesize 5000000 -analyzeduration 500000 " +
        $"-hwaccel auto -f mpegts -i pipe:0 -an -vf \"fps={FramesPerSecond},{Fit}\" {Output}", segments);

    public static LivePreview FromCapture(string ffmpeg, MonitorInfo monitor, IntPtr window, bool ddagrab) => new(ffmpeg,
        $"-hide_banner -loglevel error -nostdin {PipelineBuilder.PreviewInput(monitor, window, ddagrab, FramesPerSecond, new Size(Width, Height), Fit)} {Output}",
        null);

    public void Start() => _ = Task.Run(() => RunAsync(stop.Token));

    async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (segments == null) await StreamAsync(null, token);
                else if (segments().LastOrDefault() is { } newest) await StreamAsync(newest, token);
                await Task.Delay(segments == null ? CaptureRetryDelay : RetryDelay, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task StreamAsync(Segment? first, CancellationToken token)
    {
        using var process = FfmpegProcess.Start(ffmpeg, arguments, redirectStdout: true);
        _ = process.StandardError.ReadToEndAsync(token);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var feed = first != null && segments != null ? FeedAsync(process, first, segments, session.Token) : Task.Delay(Timeout.Infinite, session.Token);
        var frames = ReadFramesAsync(process, session.Token);
        await Task.WhenAny(feed, frames);
        session.Cancel();
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { await Task.WhenAll(feed, frames); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    static async Task FeedAsync(Process process, Segment first, Func<IReadOnlyList<Segment>> segments, CancellationToken token)
    {
        var input = process.StandardInput.BaseStream;
        var buffer = new byte[ChunkSize];
        var session = Path.GetDirectoryName(first.Path);
        var current = first;
        long position = 0;
        while (!token.IsCancellationRequested)
        {
            int read = ReadAt(current.Path, position, buffer);
            if (read > 0)
            {
                position += read;
                await input.WriteAsync(buffer.AsMemory(0, read), token);
                await input.FlushAsync(token);
                continue;
            }

            var available = segments();
            if (available.Count == 0 || Path.GetDirectoryName(available[^1].Path) != session) return;
            var next = available.FirstOrDefault(s => s.Index > current.Index);
            if (next == null)
            {
                await Task.Delay(PollDelay, token);
                continue;
            }
            while ((read = ReadAt(current.Path, position, buffer)) > 0)
            {
                position += read;
                await input.WriteAsync(buffer.AsMemory(0, read), token);
            }
            current = next;
            position = 0;
        }
    }

    async Task ReadFramesAsync(Process process, CancellationToken token)
    {
        var output = process.StandardOutput.BaseStream;
        var frame = new byte[FrameBytes];
        while (!token.IsCancellationRequested)
        {
            int filled = 0;
            while (filled < FrameBytes)
            {
                int read = await output.ReadAsync(frame.AsMemory(filled), token);
                if (read == 0) return;
                filled += read;
            }
            FrameReady?.Invoke(ToBitmap(frame));
        }
    }

    static int ReadAt(string path, long position, byte[] buffer)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length <= position) return 0;
            file.Position = position;
            return file.Read(buffer, 0, buffer.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    static Bitmap ToBitmap(byte[] bgr)
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int row = 0; row < Height; row++)
                Marshal.Copy(bgr, row * Width * 3, data.Scan0 + row * data.Stride, Width * 3);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    public void Dispose() => stop.Cancel();
}
