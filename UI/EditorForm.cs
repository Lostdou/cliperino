using static Cliperino.Core.Lang;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Cliperino.Capture;
using Cliperino.Core;
using Cliperino.Ffmpeg;

namespace Cliperino.UI;

sealed class EditorForm : Form
{
    const string ClipHost = "clip.cliperino";
    const string PageResource = "Cliperino.editor.html";
    const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    static readonly Color Background = Color.FromArgb(20, 20, 20);
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    sealed record EditorMessage(string Action, double Start, double End, bool RemoveAudio, bool Exact, bool Replace);

    sealed record ClipInfo(bool HasAudio, VideoCodec Codec, double Fps);

    readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    readonly string ffmpeg;
    readonly string clipPath;
    readonly Func<VideoCodec, string> exactEncoderArgs;
    string? lastOutput;
    bool exporting;

    public EditorForm(string ffmpeg, string clipPath, Func<VideoCodec, string> exactEncoderArgs)
    {
        this.ffmpeg = ffmpeg;
        this.clipPath = clipPath;
        this.exactEncoderArgs = exactEncoderArgs;
        Text = $"Cliperino - {Path.GetFileName(clipPath)}";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1100, 760);
        MinimumSize = new Size(720, 480);
        BackColor = Background;
        Controls.Add(view);
        Shown += async (_, _) => await InitializeAsync();
    }

    string ClipUrl =>
        $"https://{ClipHost}/{Uri.EscapeDataString(Path.GetFileName(clipPath))}?v={File.GetLastWriteTimeUtc(clipPath).Ticks}";

    async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Data, "webview2"));
            await view.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, T($"El editor necesita Microsoft Edge WebView2 Runtime.\n\nInstalalo desde {WebView2Download} y volvé a abrirlo.", $"The editor needs the Microsoft Edge WebView2 Runtime.\n\nInstall it from {WebView2Download} and open the editor again."),
                "Cliperino", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        var core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(ClipHost, Path.GetDirectoryName(clipPath)!, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        var info = await ProbeAsync();
        core.NavigationCompleted += (_, _) => Post(new { type = "load", url = ClipUrl, hasAudio = info.HasAudio, fps = info.Fps });
        core.NavigateToString(ReadPage());
    }

    async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = JsonSerializer.Deserialize<EditorMessage>(e.WebMessageAsJson, Json);
        if (message?.Action == "reveal") Process.Start("explorer.exe", $"/select,\"{lastOutput ?? clipPath}\"");
        else if (message?.Action == "export") await ExportAsync(message);
    }

    async Task ExportAsync(EditorMessage message)
    {
        if (exporting) return;
        exporting = true;
        var edit = new ClipEdit(message.Start, message.End, message.RemoveAudio, message.Exact, message.Replace);
        try
        {
            var codec = (await ProbeAsync()).Codec;
            var output = await Task.Run(() => ClipEditor.ExportAsync(ffmpeg, clipPath, edit, codec, exactEncoderArgs(codec)));
            lastOutput = output;
            if (edit.Replace)
            {
                var hasAudio = (await ProbeAsync()).HasAudio;
                Post(new { type = "done", text = T("Listo, se reemplazó el original.", "Done, the original was replaced."), reload = true, url = ClipUrl, hasAudio });
            }
            else
            {
                Post(new { type = "done", text = T($"Listo, se guardó como {Path.GetFileName(output)}", $"Done, saved as {Path.GetFileName(output)}"), reload = false });
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Error editando {clipPath}: {ex}");
            Post(new { type = "error", text = T($"No se pudo guardar: {ex.Message}", $"Couldn't save: {ex.Message}"), reload = edit.Replace, url = ClipUrl });
        }
        finally
        {
            exporting = false;
        }
    }

    async Task<ClipInfo> ProbeAsync()
    {
        var (_, output) = await FfmpegProcess.RunAsync(ffmpeg, $"-hide_banner -nostdin -i \"{clipPath}\"", TimeSpan.FromSeconds(10));
        var fps = Regex.Match(output, @"Video:.*?([\d.]+) fps");
        return new ClipInfo(
            output.Contains("Audio:"),
            output.Contains("Video: hevc") ? VideoCodec.Hevc : VideoCodec.H264,
            fps.Success && double.TryParse(fps.Groups[1].Value, CultureInfo.InvariantCulture, out var value) ? value : 60);
    }

    void Post(object message)
    {
        if (IsDisposed || view.CoreWebView2 == null) return;
        view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    static string ReadPage()
    {
        using var stream = typeof(EditorForm).Assembly.GetManifestResourceStream(PageResource)!;
        using var reader = new StreamReader(stream);
        var page = reader.ReadToEnd();
        return English ? page.Replace("<html lang=\"es\">", "<html lang=\"en\">") : page;
    }
}
