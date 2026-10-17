using System.Diagnostics;

namespace Cliperino.Capture;

public sealed record OpenWindow(string Title, string Executable, string? Path);

public sealed record GameWindow(IntPtr Handle, int ProcessId, string Executable)
{
    public static IReadOnlyList<OpenWindow> ListOpen()
    {
        var windows = new List<OpenWindow>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    var title = process.MainWindowTitle.Trim();
                    if (title.Length == 0) continue;
                    windows.Add(new OpenWindow(title, process.ProcessName + ".exe", PathOf(process)));
                }
                catch (InvalidOperationException)
                {
                    // el proceso termino mientras se lo consultaba
                }
            }
        }
        return windows.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string? PathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // procesos elevados o protegidos por anticheat no dejan leer su ruta
            return null;
        }
    }

    public static GameWindow? Find(IEnumerable<string> executables)
    {
        foreach (var executable in executables)
        {
            var name = Path.GetFileNameWithoutExtension(executable);
            if (name.Length == 0) continue;
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                            return new GameWindow(process.MainWindowHandle, process.Id, process.ProcessName + ".exe");
                    }
                    catch (InvalidOperationException)
                    {
                        // el proceso termino mientras se lo consultaba
                    }
                }
            }
        }
        return null;
    }
}
