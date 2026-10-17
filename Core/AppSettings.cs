using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Cliperino.Core;

public enum EncoderPreference { Auto, Nvenc, Amf, Qsv, X264 }

public enum CaptureMode { Window, Monitor }

public enum AudioSource { Desktop, Game }

public sealed class AppSettings
{
    public int ReplaySeconds { get; set; } = 60;
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 20;
    public string? MonitorDeviceName { get; set; }
    public EncoderPreference Encoder { get; set; } = EncoderPreference.Auto;
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Window;
    public List<string> GameExecutables { get; set; } = ["VALORANT-Win64-Shipping.exe"];
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

    public string? CachedPipelineId { get; set; }
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
        ReplaySeconds != other.ReplaySeconds || Fps != other.Fps || BitrateMbps != other.BitrateMbps ||
        MonitorDeviceName != other.MonitorDeviceName || Encoder != other.Encoder ||
        CaptureSystemAudio != other.CaptureSystemAudio || CaptureMicrophone != other.CaptureMicrophone ||
        MicrophoneDeviceId != other.MicrophoneDeviceId || CaptureMode != other.CaptureMode || AudioSource != other.AudioSource ||
        !GameExecutables.SequenceEqual(other.GameExecutables, StringComparer.OrdinalIgnoreCase);

    AppSettings Normalized()
    {
        ReplaySeconds = Math.Clamp(ReplaySeconds, 5, 1200);
        Fps = Fps is 30 or 60 ? Fps : 60;
        BitrateMbps = Math.Clamp(BitrateMbps, 2, 150);
        AudioOffsetMs = Math.Clamp(AudioOffsetMs, -2000, 2000);
        if (string.IsNullOrWhiteSpace(OutputFolder)) OutputFolder = AppPaths.DefaultClips;
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Ctrl+Alt+F10";
        GameExecutables = GameExecutables?.Select(g => g.Trim()).Where(g => g.Length > 0).ToList() ?? [];
        return this;
    }

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
