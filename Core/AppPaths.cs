namespace Cliperino.Core;

static class AppPaths
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static readonly string Data = Path.Combine(Local, "Cliperino");
    public static readonly string Buffer = Path.Combine(Data, "buffer");
    public static readonly string Temp = Path.Combine(Data, "temp");
    public static readonly string Logs = Path.Combine(Data, "logs");
    public static readonly string FfmpegDir = Path.Combine(Data, "ffmpeg");
    public static readonly string SettingsFile = Path.Combine(Roaming, "Cliperino", "settings.json");
    public static readonly string DefaultClips =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Cliperino");
}
