using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Cliperino.Audio;
using Cliperino.Capture;
using Cliperino.Core;
using Cliperino.Ffmpeg;

namespace Cliperino.UI;

sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem statusItem, pipelineItem, toggleItem, saveItem;
    readonly HotkeyWindow hotkey = new();
    readonly Icon idleIcon = MakeIcon(Color.Gray), recordingIcon = MakeIcon(Color.Red), busyIcon = MakeIcon(Color.Orange);
    readonly SynchronizationContext ui;
    AppSettings settings = AppSettings.Load();
    string? ffmpeg;
    FfmpegCapabilities? caps;
    VideoBuffer? video;
    IAudioRecorder? systemAudio, micAudio;
    ResolvedPipeline? pipeline;
    GameWindow? captureWindow;
    IntPtr failedWindow;
    readonly System.Windows.Forms.Timer gameWatcher = new() { Interval = 3000 };
    SettingsForm? settingsForm;
    PreviewForm? previewForm;
    bool starting, saving, retriedAfterFailure;
    string? lastClip;

    bool IsRunning => video != null;

    public TrayApp()
    {
        statusItem = new ToolStripMenuItem("Iniciando…") { Enabled = false };
        pipelineItem = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        toggleItem = new ToolStripMenuItem("Activar repetición instantánea", null, async (_, _) => await ToggleAsync());
        saveItem = new ToolStripMenuItem("Guardar clip", null, async (_, _) => await SaveClipAsync()) { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem, pipelineItem, new ToolStripSeparator(),
            toggleItem, saveItem,
            new ToolStripMenuItem("Vista previa", null, (_, _) => ShowPreview()),
            new ToolStripMenuItem("Abrir carpeta de clips", null, (_, _) => OpenClipsFolder()),
            new ToolStripMenuItem("Ajustes…", null, (_, _) => ShowSettings()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Salir", null, (_, _) => ExitApp()),
        });
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        tray = new NotifyIcon { Icon = idleIcon, Text = "Cliperino", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => OpenClipsFolder();
        tray.BalloonTipClicked += (_, _) =>
        {
            if (lastClip != null && File.Exists(lastClip)) Process.Start("explorer.exe", $"/select,\"{lastClip}\"");
        };

        hotkey.Pressed += async () => await SaveClipAsync();
        RegisterHotkey();
        gameWatcher.Tick += (_, _) => WatchGame();
        gameWatcher.Start();
        _ = InitializeAsync();
    }

    async Task InitializeAsync()
    {
        try
        {
            ffmpeg = FfmpegLocator.Find();
            if (ffmpeg == null)
            {
                var answer = MessageBox.Show(
                    "Cliperino necesita ffmpeg para grabar y no lo encontró.\n\n¿Descargarlo ahora? (~100 MB, build de gyan.dev)",
                    "Cliperino", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    SetStatus("ffmpeg no encontrado", idleIcon);
                    return;
                }
                ffmpeg = await DownloadFfmpegAsync();
                if (ffmpeg == null)
                {
                    SetStatus("ffmpeg no encontrado", idleIcon);
                    return;
                }
            }
            caps = await FfmpegLocator.InspectAsync(ffmpeg);
            Log.Write($"ffmpeg: {ffmpeg} – {caps}");
            if (!caps.HasDdagrab) Log.Write("Este ffmpeg no tiene ddagrab (requiere ffmpeg 6+); sólo se podrá usar gdigrab.");

            if (settings.StartReplayOnLaunch) await StartReplayAsync(force: false);
            else SetStatus("Repetición desactivada", idleIcon);
        }
        catch (Exception ex)
        {
            Log.Write($"Error al iniciar: {ex}");
            SetStatus("Error al iniciar", idleIcon);
            Notify($"Error al iniciar: {ex.Message}", ToolTipIcon.Error);
        }
    }

    async Task<string?> DownloadFfmpegAsync()
    {
        using var window = new DownloadForm();
        var progress = new Progress<(string Text, int? Percent)>(p =>
        {
            window.Report(p.Text, p.Percent);
            SetStatus(p.Text, busyIcon);
        });
        window.Show();
        try
        {
            return await FfmpegLocator.DownloadAsync(progress, window.Cancellation);
        }
        catch (OperationCanceledException)
        {
            Log.Write("Descarga de ffmpeg cancelada.");
            return null;
        }
        finally
        {
            window.Close();
        }
    }

    async Task StartReplayAsync(bool force)
    {
        if (IsRunning || starting || ffmpeg == null || caps == null) return;
        starting = true;
        try
        {
            SetStatus("Detectando GPU…", busyIcon);
            var (path, capabilities, current) = (ffmpeg, caps, settings);
            var progress = new Progress<string>(msg => SetStatus(msg, busyIcon));
            var resolved = await Task.Run(() => PipelineBuilder.ResolveAsync(path, capabilities, current, force, progress));
            if (resolved == null)
            {
                SetStatus("No se pudo iniciar la captura", idleIcon);
                Notify(@"Ningún método de captura funcionó. Revisá %LOCALAPPDATA%\Cliperino\logs\probe.log", ToolTipIcon.Error);
                return;
            }
            settings.Save(); // persiste la cache del pipeline
            pipeline = resolved;
            var window = captureWindow = FindGameWindow();

            // wasapi da problemas si se inicializa en el hilo sta de la ui
            var bufferSeconds = settings.ReplaySeconds + 10;
            await Task.Run(async () =>
            {
                if (current.CaptureSystemAudio) systemAudio = await CreateSystemAudioAsync(current, window, bufferSeconds);
                if (current.CaptureMicrophone) micAudio = new AudioRecorder(DataFlow.Capture, current.MicrophoneDeviceId, bufferSeconds);
            });

            var args = PipelineBuilder.BuildArgs(resolved.Id, resolved.Monitor, current.Fps, current.BitrateMbps, window?.Handle ?? IntPtr.Zero);
            video = new VideoBuffer(path, args, current.ReplaySeconds);
            video.Failed += OnVideoFailed;
            video.Start();
            var what = window?.Executable ?? "el monitor";
            SetStatus($"Grabando {what}: últimos {FormatDuration(current.ReplaySeconds)}", recordingIcon);
            // windows 11 esconde los iconos nuevos de la bandeja, sin aviso no se sabe que esta grabando
            Notify($"Grabando {what}: últimos {FormatDuration(current.ReplaySeconds)}. {current.Hotkey} guarda un clip.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write($"Error al iniciar la captura: {ex}");
            StopReplay();
            SetStatus("Error al iniciar la captura", idleIcon);
            Notify(ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            starting = false;
            UpdateMenu();
        }
    }

    GameWindow? FindGameWindow()
    {
        if (settings.CaptureMode != CaptureMode.Window || caps?.HasGfxcapture != true || pipeline?.CapturesWithGpu != true) return null;
        var window = GameWindow.Find(settings.GameExecutables);
        return window?.Handle == failedWindow ? null : window;
    }

    static async Task<IAudioRecorder> CreateSystemAudioAsync(AppSettings s, GameWindow? window, double seconds)
    {
        if (s.AudioSource == AudioSource.Game && window != null)
        {
            try
            {
                return await ProcessAudioRecorder.StartAsync(window.ProcessId, seconds);
            }
            catch (Exception ex)
            {
                Log.Write($"No se pudo capturar el audio de {window.Executable}, se usa el del escritorio: {ex.Message}");
            }
        }
        return new AudioRecorder(DataFlow.Render, null, seconds);
    }

    void WatchGame()
    {
        if (!IsRunning || starting || saving) return;
        var window = FindGameWindow();
        if (window?.Handle == captureWindow?.Handle) return;
        Log.Write(window != null ? $"Juego detectado: {window.Executable}" : "El juego se cerró, se vuelve a capturar el monitor.");
        StopReplay();
        _ = StartReplayAsync(force: false);
    }

    void StopReplay()
    {
        var (v, sys, mic) = (video, systemAudio, micAudio);
        (video, systemAudio, micAudio, pipeline, captureWindow) = (null, null, null, null, null);
        if (v != null)
        {
            v.Failed -= OnVideoFailed;
            v.Dispose();
        }
        sys?.Dispose();
        mic?.Dispose();
        SetStatus("Repetición desactivada", idleIcon);
        UpdateMenu();
    }

    void OnVideoFailed(string error) => ui.Post(async _ =>
    {
        Log.Write($"La captura falló: {error}");
        var window = captureWindow;
        StopReplay();
        if (window != null)
        {
            failedWindow = window.Handle;
            Notify($"No se pudo capturar la ventana de {window.Executable}, se captura el monitor.", ToolTipIcon.Warning);
            await StartReplayAsync(force: false);
            return;
        }
        if (!retriedAfterFailure)
        {
            // puede ser un driver actualizado o un cambio de gpu
            retriedAfterFailure = true;
            Notify("La captura falló; volviendo a detectar la GPU…", ToolTipIcon.Warning);
            await StartReplayAsync(force: true);
        }
        else
        {
            SetStatus("Captura detenida por un error", idleIcon);
            Notify($"La captura se detuvo: {error}", ToolTipIcon.Error);
        }
    }, null);

    async Task ToggleAsync()
    {
        retriedAfterFailure = false;
        failedWindow = IntPtr.Zero;
        if (IsRunning) StopReplay();
        else await StartReplayAsync(force: false);
    }

    async Task SaveClipAsync()
    {
        if (ffmpeg == null || video == null)
        {
            Notify("La repetición instantánea está desactivada.", ToolTipIcon.Warning);
            return;
        }
        if (saving)
        {
            Notify("Ya se está guardando un clip…", ToolTipIcon.Info);
            return;
        }
        saving = true;
        tray.Icon = busyIcon;
        try
        {
            var (path, v, s) = (ffmpeg, video, settings.Clone());
            var (sys, mic) = (systemAudio?.Ring, micAudio?.Ring);
            lastClip = await Task.Run(() => ClipSaver.SaveAsync(path, v, sys, mic, s));
            SystemSounds.Asterisk.Play();
            Notify($"Clip guardado: {Path.GetFileName(lastClip)}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write($"Error guardando el clip: {ex}");
            Notify($"No se pudo guardar el clip: {ex.Message}", ToolTipIcon.Error);
        }
        finally
        {
            saving = false;
            tray.Icon = IsRunning ? recordingIcon : idleIcon;
        }
    }

    void ShowSettings()
    {
        if (settingsForm != null)
        {
            settingsForm.Activate();
            return;
        }
        hotkey.Unregister(); // si sigue registrado, el formulario no recibe la combinacion
        try
        {
            using var form = new SettingsForm(settings.Clone(), GpuInfo.Enumerate(), pipeline?.Description, AudioRecorder.ListMicrophones());
            settingsForm = form;
            if (form.ShowDialog() != DialogResult.OK) return;

            var updated = form.Result;
            bool restart = IsRunning && (form.ForceRedetect || updated.CaptureChanged(settings));
            if (form.ForceRedetect) updated.CachedPipelineId = null;
            failedWindow = IntPtr.Zero;
            settings = updated;
            settings.Save();
            settings.ApplyStartWithWindows();
            if (restart)
            {
                StopReplay();
                _ = StartReplayAsync(form.ForceRedetect);
            }
        }
        finally
        {
            settingsForm = null;
            RegisterHotkey();
        }
    }

    void ShowPreview()
    {
        if (previewForm != null)
        {
            previewForm.Activate();
            return;
        }
        previewForm = new PreviewForm(PreviewState);
        previewForm.FormClosed += (_, _) => previewForm = null;
        previewForm.Show();
    }

    PreviewSource PreviewState()
    {
        if (video == null) return new PreviewSource(null, null, "La repetición instantánea está desactivada.");
        var what = captureWindow != null ? $"Juego: {captureWindow.Executable}" : "Pantalla";
        return new PreviewSource(ffmpeg, video.ListSegments().LastOrDefault()?.Path, $"{what} · {pipeline?.Description} · se actualiza cada 2 s");
    }

    void RegisterHotkey()
    {
        if (hotkey.Register(settings.Hotkey)) Log.Write($"Atajo registrado: {settings.Hotkey}");
        else
        {
            Log.Write($"No se pudo registrar el atajo {settings.Hotkey}.");
            Notify($"No se pudo registrar el atajo {settings.Hotkey} (¿lo usa otra app?). Cambialo en Ajustes.", ToolTipIcon.Warning);
        }
        saveItem.Text = $"Guardar clip ({settings.Hotkey})";
    }

    void OpenClipsFolder()
    {
        Directory.CreateDirectory(settings.OutputFolder);
        Process.Start("explorer.exe", $"\"{settings.OutputFolder}\"");
    }

    void ExitApp()
    {
        gameWatcher.Dispose();
        previewForm?.Close();
        StopReplay();
        hotkey.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    void SetStatus(string text, Icon icon)
    {
        statusItem.Text = text;
        pipelineItem.Text = captureWindow != null ? $"{pipeline?.Description} · ventana de {captureWindow.Executable}" : pipeline?.Description ?? "";
        pipelineItem.Visible = pipeline != null;
        var tip = $"Cliperino – {text}";
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
        tray.Icon = icon;
    }

    void UpdateMenu()
    {
        toggleItem.Text = IsRunning ? "Desactivar repetición instantánea" : "Activar repetición instantánea";
        saveItem.Enabled = IsRunning;
    }

    void Notify(string text, ToolTipIcon icon) =>
        tray.ShowBalloonTip(4000, "Cliperino", text.Length > 250 ? text[..250] : text, icon);

    public static string FormatDuration(int seconds) =>
        seconds < 60 ? $"{seconds} s" : $"{seconds / 60}:{seconds % 60:00} min";

    static Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            using var border = new Pen(Color.White, 3);
            g.FillEllipse(fill, 3, 3, 26, 26);
            g.DrawEllipse(border, 3, 3, 26, 26);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
