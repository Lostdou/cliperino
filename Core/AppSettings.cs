using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Cliperino.Core;

public enum EncoderPreference { Auto, Nvenc, Amf, Qsv, X264 }

public enum CaptureMode { Window, Monitor }

public enum AudioSource { Desktop, Game }

public enum Language { Auto, Spanish, English }

public enum FeedbackStyle { Overlay, Sound }

public enum VideoCodec { H264, Hevc }

public enum QualityPreset { Low, Balanced, High, Custom }

public sealed class AppSettings
{
    public int ReplaySeconds { get; set; } = 60;
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 20;
    public QualityPreset? Quality { get; set; }
    public string? MonitorDeviceName { get; set; }
    public EncoderPreference Encoder { get; set; } = EncoderPreference.Auto;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Window;
    public List<string> GameExecutables { get; set; } = ["VALORANT-Win64-Shipping.exe"];
    public Dictionary<string, string> GameAliases { get; set; } = [];
    public AudioSource AudioSource { get; set; } = AudioSource.Desktop;
    public string Hotkey { get; set; } = "Ctrl+Alt+F10";
    public string OutputFolder { get; set; } = AppPaths.DefaultClips;
    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public int AudioOffsetMs { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartReplayOnLaunch { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public Language Language { get; set; } = Language.Auto;
    public FeedbackStyle Feedback { get; set; } = FeedbackStyle.Overlay;
    public bool FolderPerGame { get; set; } = true;

    public string? CachedPipelineId { get; set; }
    public VideoCodec? CachedPipelineCodec { get; set; }
    public string? PipelineFingerprint { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json);
                if (loaded != null) return loaded.Normalized();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"No se pudo leer settings.json, se usan los valores por defecto: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    public bool CaptureChanged(AppSettings other) =>
        ReplaySeconds != other.ReplaySeconds || Fps != other.Fps || BitrateMbps != other.BitrateMbps || EffectiveQuality != other.EffectiveQuality ||
        MonitorDeviceName != other.MonitorDeviceName || Encoder != other.Encoder || Codec != other.Codec ||
        CaptureSystemAudio != other.CaptureSystemAudio || CaptureMicrophone != other.CaptureMicrophone ||
        MicrophoneDeviceId != other.MicrophoneDeviceId || CaptureMode != other.CaptureMode || AudioSource != other.AudioSource ||
        !GameExecutables.SequenceEqual(other.GameExecutables, StringComparer.OrdinalIgnoreCase);

    AppSettings Normalized()
    {
        ReplaySeconds = Math.Clamp(ReplaySeconds, 5, 1200);
        Fps = Fps is 30 or 60 or 120 ? Fps : 60;
        BitrateMbps = Math.Clamp(BitrateMbps, 2, 150);
        AudioOffsetMs = Math.Clamp(AudioOffsetMs, -2000, 2000);
        if (string.IsNullOrWhiteSpace(OutputFolder)) OutputFolder = AppPaths.DefaultClips;
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Ctrl+Alt+F10";
        GameExecutables = GameExecutables?.Select(g => g.Trim()).Where(g => g.Length > 0).ToList() ?? [];
        GameAliases ??= [];
        Quality = EffectiveQuality;
        return this;
    }

    [JsonIgnore]
    public QualityPreset EffectiveQuality => Quality ?? (BitrateMbps == 20 ? QualityPreset.Balanced : QualityPreset.Custom);

    public int VideoBitrate(Size? resolution, VideoCodec codec) =>
        BitrateFor(EffectiveQuality, BitrateMbps, resolution ?? new Size(1920, 1080), Fps, codec);

    public static int BitrateFor(QualityPreset preset, int customMbps, Size resolution, int fps, VideoCodec codec)
    {
        if (preset == QualityPreset.Custom) return customMbps;
        double baseMbps = preset switch { QualityPreset.Low => 10, QualityPreset.High => 35, _ => 20 };
        double pixels = Math.Max(1, resolution.Width * resolution.Height) / (1920.0 * 1080.0);
        double mbps = baseMbps * Math.Pow(pixels, 0.85) * Math.Pow(fps / 60.0, 0.8) * (codec == VideoCodec.Hevc ? 0.65 : 1);
        return (int)Math.Clamp(Math.Round(mbps, MidpointRounding.AwayFromZero), 2, 150);
    }

    public string? AliasOf(string executable) =>
        GameAliases.FirstOrDefault(a => string.Equals(a.Key, executable, StringComparison.OrdinalIgnoreCase)).Value;

    public string DisplayNameOf(string executable) => AliasOf(executable) ?? executable;

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void ApplyStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartWithWindows) key.SetValue("Cliperino", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Cliperino", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write($"No se pudo actualizar el inicio con Windows: {ex.Message}");
        }
    }
}
