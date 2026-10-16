namespace Cliperino.Core;

static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(AppPaths.Logs, "cliperino.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 5_000_000) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // el log nunca debe tumbar la app
            }
        }
    }
}
