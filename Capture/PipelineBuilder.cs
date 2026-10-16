using System.Text;
using Cliperino.Core;
using Cliperino.Ffmpeg;

namespace Cliperino.Capture;

public sealed record ResolvedPipeline(string Id, string Description, string Args);

// ids: dda-zc-<encoder> (zero-copy), dda-sys-<encoder> (via memoria del sistema), gdi-x264
public static class PipelineBuilder
{
    static readonly Dictionary<GpuVendor, string> VendorEncoder = new()
    {
        [GpuVendor.Nvidia] = "nvenc",
        [GpuVendor.Amd] = "amf",
        [GpuVendor.Intel] = "qsv",
    };

    public static List<string> Candidates(MonitorInfo? monitor, GpuSnapshot gpus, FfmpegCapabilities caps, EncoderPreference preference)
    {
        var list = new List<string>();
        if (caps.HasDdagrab && monitor != null)
        {
            // via memoria como respaldo por si el driver rechaza el zero-copy
            if (VendorEncoder.TryGetValue(monitor.Adapter.Vendor, out var own) && caps.HasEncoder(own))
            {
                list.Add($"dda-zc-{own}");
                list.Add($"dda-sys-{own}");
            }
            // laptops hibridas: pantalla en la igpu, encoder en la dgpu
            foreach (var vendor in gpus.Adapters.Select(a => a.Vendor).Distinct())
            {
                if (vendor != monitor.Adapter.Vendor && VendorEncoder.TryGetValue(vendor, out var other) && caps.HasEncoder(other))
                    list.Add($"dda-sys-{other}");
            }
            if (caps.HasX264) list.Add("dda-sys-x264");
        }
        if (caps.HasGdigrab && caps.HasX264) list.Add("gdi-x264");

        var wanted = preference switch
        {
            EncoderPreference.Nvenc => "nvenc",
            EncoderPreference.Amf => "amf",
            EncoderPreference.Qsv => "qsv",
            EncoderPreference.X264 => "x264",
            _ => null,
        };
        if (wanted == null) return list;
        // el resto queda de respaldo para que la app siga grabando si el forzado falla
        return list.Where(id => id.EndsWith(wanted)).Concat(list.Where(id => !id.EndsWith(wanted))).ToList();
    }

    public static string BuildArgs(string id, MonitorInfo? monitor, int fps, int bitrateMbps)
    {
        var parts = id.Split('-');
        var encoder = parts[^1];
        string input;
        if (parts[0] == "dda")
        {
            if (monitor == null) throw new InvalidOperationException("No se encontró ningún monitor para capturar.");
            var conversion = (parts[1], encoder) switch
            {
                ("zc", "qsv") => ",hwmap=derive_device=qsv,format=qsv,vpp_qsv=format=nv12",
                ("zc", _) => "", // nvenc y amf aceptan texturas d3d11 directo
                (_, "x264") => ",hwdownload,format=bgra,format=yuv420p",
                _ => ",hwdownload,format=bgra,format=nv12",
            };
            // sin -filter_hw_device ddagrab usa el adaptador 0 y falla en sistemas multi-gpu
            input = $"-init_hw_device d3d11va=cap:{monitor.Adapter.Index} -filter_hw_device cap " +
                    $"-filter_complex \"ddagrab=output_idx={monitor.OutputIndex}:framerate={fps}{conversion}[v]\" -map \"[v]\"";
        }
        else
        {
            var area = monitor == null ? "" :
                $"-offset_x {monitor.Bounds.X} -offset_y {monitor.Bounds.Y} -video_size {monitor.Bounds.Width}x{monitor.Bounds.Height} ";
            input = $"-f gdigrab -framerate {fps} -draw_mouse 1 {area}-i desktop -vf format=yuv420p";
        }

        var encoderArgs = encoder switch
        {
            "nvenc" => "-c:v h264_nvenc -preset p4 -tune ll -rc cbr -bf 0 -delay 0",
            "amf" => "-c:v h264_amf -usage lowlatency -quality speed -rc cbr",
            "qsv" => "-c:v h264_qsv -preset veryfast -async_depth 1",
            _ => "-c:v libx264 -preset ultrafast -tune zerolatency",
        };
        // keyframe cada segundo para poder cortar segmentos de 2 s
        return $"{input} {encoderArgs} -g {fps} -b:v {bitrateMbps}M -maxrate {bitrateMbps}M -bufsize {bitrateMbps * 2}M";
    }

    public static string Describe(string id, MonitorInfo? monitor, GpuSnapshot gpus)
    {
        var parts = id.Split('-');
        var encoder = parts[^1];
        if (encoder == "x264") return parts[0] == "gdi" ? "CPU (x264) con captura GDI" : "CPU (x264)";
        var vendor = VendorEncoder.First(kv => kv.Value == encoder).Key;
        var name = encoder switch { "nvenc" => "NVENC", "amf" => "AMF", _ => "Quick Sync" };
        if (parts[1] == "zc") return $"{name} en {monitor?.Adapter.Name} (zero-copy)";
        var gpu = gpus.Adapters.FirstOrDefault(a => a.Vendor == vendor)?.Name ?? vendor.ToString();
        return $"{name} en {gpu} (vía memoria)";
    }

    public static async Task<ResolvedPipeline?> ResolveAsync(
        string ffmpeg, FfmpegCapabilities caps, AppSettings settings, bool force, IProgress<string>? progress)
    {
        var gpus = GpuInfo.Enumerate();
        var monitor = gpus.FindMonitor(settings.MonitorDeviceName);
        var fingerprint = Fingerprint(gpus, monitor, caps, settings.Encoder, ffmpeg);

        if (!force && settings.CachedPipelineId != null && settings.PipelineFingerprint == fingerprint)
            return Make(settings.CachedPipelineId);

        var report = new StringBuilder();
        AppendHeader(report, ffmpeg, caps, gpus, monitor);
        ResolvedPipeline? result = null;
        foreach (var id in Candidates(monitor, gpus, caps, settings.Encoder))
        {
            progress?.Report($"Probando {Describe(id, monitor, gpus)}…");
            var args = BuildArgs(id, monitor, settings.Fps, settings.BitrateMbps);
            var (code, output) = await TestAsync(ffmpeg, args);
            AppendResult(report, id, args, code, output);
            if (code != 0) continue;
            settings.CachedPipelineId = id;
            settings.PipelineFingerprint = fingerprint;
            result = Make(id);
            break;
        }
        WriteProbeLog(report);
        Log.Write(result != null ? $"Pipeline elegido: {result.Id} ({result.Description})" : "Ningún pipeline funcionó; ver probe.log");
        return result;

        ResolvedPipeline Make(string id) =>
            new(id, Describe(id, monitor, gpus), BuildArgs(id, monitor, settings.Fps, settings.BitrateMbps));
    }

    public static async Task<int> RunDiagnosticsAsync(TextWriter output)
    {
        var ffmpeg = FfmpegLocator.Find();
        if (ffmpeg == null)
        {
            output.WriteLine("ffmpeg no encontrado. Abrí Cliperino normalmente para descargarlo.");
            return 1;
        }
        var caps = await FfmpegLocator.InspectAsync(ffmpeg);
        var gpus = GpuInfo.Enumerate();
        var settings = AppSettings.Load();
        var monitor = gpus.FindMonitor(settings.MonitorDeviceName);

        var report = new StringBuilder();
        AppendHeader(report, ffmpeg, caps, gpus, monitor);
        output.Write(report);
        int ok = 0;
        foreach (var id in Candidates(monitor, gpus, caps, EncoderPreference.Auto))
        {
            var args = BuildArgs(id, monitor, settings.Fps, settings.BitrateMbps);
            var (code, log) = await TestAsync(ffmpeg, args);
            AppendResult(report, id, args, code, log);
            output.WriteLine($"  {(code == 0 ? "OK   " : "FALLA")} {id,-14} {Describe(id, monitor, gpus)}");
            if (code == 0) ok++;
            else output.WriteLine($"        {log.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()}");
        }
        var path = WriteProbeLog(report);
        output.WriteLine($"{ok} pipeline(s) funcionan. Log completo: {path}");
        return ok > 0 ? 0 : 2;
    }

    static Task<(int ExitCode, string Output)> TestAsync(string ffmpeg, string args) =>
        FfmpegProcess.RunAsync(ffmpeg, $"-hide_banner -loglevel error -nostdin {args} -t 2 -f null -", TimeSpan.FromSeconds(20));

    static string Fingerprint(GpuSnapshot gpus, MonitorInfo? monitor, FfmpegCapabilities caps, EncoderPreference preference, string ffmpeg) =>
        string.Join(";", gpus.Adapters.Select(a => $"{a.Index}:{a.VendorId:X4}:{a.DeviceId:X4}")) +
        $"|{monitor?.DeviceName}@{monitor?.Adapter.Index}:{monitor?.OutputIndex}|{caps.Version}|{preference}|{ffmpeg}";

    static void AppendHeader(StringBuilder sb, string ffmpeg, FfmpegCapabilities caps, GpuSnapshot gpus, MonitorInfo? monitor)
    {
        sb.AppendLine($"Cliperino probe – {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"ffmpeg: {ffmpeg}");
        sb.AppendLine($"  {caps.Version}");
        sb.AppendLine($"  ddagrab={caps.HasDdagrab} gdigrab={caps.HasGdigrab} nvenc={caps.HasNvenc} amf={caps.HasAmf} qsv={caps.HasQsv} x264={caps.HasX264}");
        sb.AppendLine("GPUs:");
        foreach (var a in gpus.Adapters)
            sb.AppendLine($"  [{a.Index}] {a.Name} ({a.Vendor}, VEN_{a.VendorId:X4} DEV_{a.DeviceId:X4})");
        sb.AppendLine("Monitores:");
        foreach (var m in gpus.Monitors)
            sb.AppendLine($"  {m.DeviceName} {m.Bounds.Width}x{m.Bounds.Height} en ({m.Bounds.X},{m.Bounds.Y}) -> GPU {m.Adapter.Index}, salida {m.OutputIndex}{(m.IsPrimary ? " (principal)" : "")}");
        sb.AppendLine($"Monitor a capturar: {monitor?.DeviceName ?? "(ninguno)"}");
        sb.AppendLine("Pipelines:");
    }

    static void AppendResult(StringBuilder sb, string id, string args, int code, string output)
    {
        sb.AppendLine($"--- {id}: {(code == 0 ? "OK" : $"FALLA (código {code})")}");
        sb.AppendLine($"    ffmpeg {args}");
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            sb.AppendLine($"    {line.TrimEnd()}");
    }

    static string WriteProbeLog(StringBuilder report)
    {
        var path = Path.Combine(AppPaths.Logs, "probe.log");
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            File.WriteAllText(path, report.ToString());
        }
        catch (IOException) { }
        return path;
    }
}
