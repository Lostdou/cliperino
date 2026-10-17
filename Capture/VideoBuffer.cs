using System.Diagnostics;
using Cliperino.Core;
using Cliperino.Ffmpeg;

namespace Cliperino.Capture;

public sealed record Segment(string Path, int Index, DateTime CreatedUtc);

public sealed class VideoBuffer : IDisposable
{
    public const int SegmentSeconds = 2;

    readonly string ffmpeg;
    readonly string pipelineArgs;
    readonly int keepSegments;
    readonly System.Threading.Timer cleanupTimer;
    readonly object cleanupGate = new();
    readonly Queue<string> lastErrors = new();
    Process? process;
    string sessionDir = "";
    DateTime launchedUtc;
    int restarts;
    int cleanupPauses;
    volatile bool stopped;

    public event Action<string>? Failed;

    public VideoBuffer(string ffmpeg, string pipelineArgs, int replaySeconds)
    {
        this.ffmpeg = ffmpeg;
        this.pipelineArgs = pipelineArgs;
        keepSegments = (int)Math.Ceiling(replaySeconds / (double)SegmentSeconds) + 3;
        cleanupTimer = new System.Threading.Timer(_ => Cleanup(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        DeleteSessions();
        Launch();
        cleanupTimer.Change(2000, 2000);
    }

    void Launch()
    {
        // carpeta nueva por sesion: ntfs hereda la fecha de creacion de archivos borrados con el mismo nombre
        sessionDir = Path.Combine(AppPaths.Buffer, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(sessionDir);
        var output = Path.Combine(sessionDir, "seg_%06d.ts");
        var args = $"-hide_banner -loglevel error -nostats -y {pipelineArgs} -an " +
                   $"-f segment -segment_time {SegmentSeconds} -segment_format mpegts \"{output}\"";
        Log.Write($"Iniciando captura: ffmpeg {args}");

        var p = FfmpegProcess.Start(ffmpeg, args);
        p.EnableRaisingEvents = true;
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Log.Write($"[ffmpeg] {e.Data}");
            lock (lastErrors)
            {
                lastErrors.Enqueue(e.Data);
                while (lastErrors.Count > 10) lastErrors.Dequeue();
            }
        };
        p.Exited += OnExited;
        p.BeginErrorReadLine();
        launchedUtc = DateTime.UtcNow;
        process = p;
    }

    void OnExited(object? sender, EventArgs e)
    {
        if (stopped) return;
        var exitCode = ((Process)sender!).ExitCode;
        string errors;
        lock (lastErrors) errors = string.Join(Environment.NewLine, lastErrors);
        Log.Write($"ffmpeg terminó inesperadamente (código {exitCode}).");

        if (DateTime.UtcNow - launchedUtc > TimeSpan.FromMinutes(1)) restarts = 0;
        if (++restarts > 3)
        {
            Failed?.Invoke(errors.Length > 0 ? errors : $"ffmpeg terminó con código {exitCode}");
            return;
        }
        Task.Delay(2000).ContinueWith(_ =>
        {
            if (stopped) return;
            try
            {
                DeleteSessions();
                Launch();
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex.Message);
            }
        });
    }

    public IReadOnlyList<Segment> ListSegments()
    {
        var dir = sessionDir;
        var list = new List<Segment>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir, "seg_*.ts"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(file).AsSpan(4), out var index))
                list.Add(new Segment(file, index, File.GetCreationTimeUtc(file)));
        }
        list.Sort((a, b) => a.Index.CompareTo(b.Index));
        return list;
    }

    public IDisposable PauseCleanup()
    {
        lock (cleanupGate) cleanupPauses++;
        return new Releaser(() => { lock (cleanupGate) cleanupPauses--; });
    }

    void Cleanup()
    {
        lock (cleanupGate)
        {
            if (cleanupPauses > 0) return;
            try
            {
                var segments = ListSegments();
                for (int i = 0; i < segments.Count - keepSegments; i++) TryDelete(segments[i].Path);
            }
            catch (Exception ex)
            {
                Log.Write($"Error limpiando el buffer: {ex.Message}");
            }
        }
    }

    void DeleteSessions()
    {
        if (!Directory.Exists(AppPaths.Buffer)) return;
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.Buffer))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        stopped = true;
        cleanupTimer.Dispose();
        var p = process;
        if (p != null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.StandardInput.Write('q');
                    p.StandardInput.Flush();
                    if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            p.Dispose();
        }
        DeleteSessions();
    }

    sealed class Releaser(Action release) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) release();
        }
    }
}
