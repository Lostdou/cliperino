using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Cliperino.Audio;
using Cliperino.Capture;
using Cliperino.Core;
using Cliperino.Ffmpeg;
using Cliperino.Update;

namespace Cliperino.UI;

sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromSeconds(30);
    static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    readonly ToolStripMenuItem statusItem, pipelineItem, toggleItem, saveItem, updateItem;
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
    readonly System.Windows.Forms.Timer updateTimer = new();
    UpdateInfo? availableUpdate;
    Version? notifiedVersion;
    bool updating;
    SettingsForm? settingsForm;
    bool starting, saving, retriedAfterFailure;
    Action? balloonClick;

    bool IsRunning => video != null;

    public TrayApp()
    {
        statusItem = new ToolStripMenuItem("Iniciando…") { Enabled = false };
        pipelineItem = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        toggleItem = new ToolStripMenuItem("Activar repetición instantánea", null, async (_, _) => await ToggleAsync());
        saveItem = new ToolStripMenuItem("Guardar clip", null, async (_, _) => await SaveClipAsync()) { Enabled = false };
        updateItem = new ToolStripMenuItem("Buscar actualizaciones", null, async (_, _) => await OnUpdateItemAsync());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem, pipelineItem, new ToolStripSeparator(),
            toggleItem, saveItem,
            new ToolStripMenuItem("Editar clip…", null, (_, _) => PickClipToEdit()),
            new ToolStripMenuItem("Abrir carpeta de clips", null, (_, _) => OpenClipsFolder()),
            new ToolStripMenuItem("Ajustes…", null, (_, _) => ShowSettings()),
            updateItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Salir", null, (_, _) => ExitApp()),
        });
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        tray = new NotifyIcon { Icon = idleIcon, Text = "Cliperino", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => OpenClipsFolder();
        tray.BalloonTipClicked += (_, _) => balloonClick?.Invoke();

        hotkey.Pressed += async () => await SaveClipAsync();
        RegisterHotkey();
        gameWatcher.Tick += (_, _) => WatchGame();
        gameWatcher.Start();
        updateTimer.Tick += async (_, _) => await OnUpdateTimerAsync();
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
                    "Para grabar, Cliperino necesita ffmpeg y no está instalado.\n\n¿Descargarlo ahora? Son unos 100 MB.",
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
            if (Updater.JustUpdated) Notify($"Cliperino se actualizó a la versión {Updater.CurrentVersion.ToString(3)}.", ToolTipIcon.Info);
            StartUpdateChecks();
        }
        catch (Exception ex)
        {
            Log.Write($"Error al iniciar: {ex}");
            SetStatus("No se pudo iniciar", idleIcon);
            Notify($"Cliperino no pudo iniciar: {ex.Message}", ToolTipIcon.Error);
        }
    }

    async Task<string?> DownloadFfmpegAsync()
    {
        using var window = new DownloadForm("Cliperino – Descargando ffmpeg");
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
            SetStatus("Preparando la grabación…", busyIcon);
            var (path, capabilities, current) = (ffmpeg, caps, settings);
            var progress = new Progress<string>(msg => SetStatus(msg, busyIcon));
            var resolved = await Task.Run(() => PipelineBuilder.ResolveAsync(path, capabilities, current, force, progress));
            if (resolved == null)
            {
                SetStatus("No se pudo empezar a grabar", idleIcon);
                Notify(@"No se pudo empezar a grabar. Los detalles quedaron en %LOCALAPPDATA%\Cliperino\logs\probe.log", ToolTipIcon.Error);
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
            var what = window?.Executable ?? "la pantalla";
            SetStatus($"Grabando {what}: últimos {FormatDuration(current.ReplaySeconds)}", recordingIcon);
            // windows 11 esconde los iconos nuevos de la bandeja, sin aviso no se sabe que esta grabando
            Notify($"Grabando {what}. Apretá {current.Hotkey} para guardar los últimos {FormatDuration(current.ReplaySeconds)}.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write($"Error al iniciar la captura: {ex}");
            StopReplay();
            SetStatus("No se pudo empezar a grabar", idleIcon);
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
            Notify($"No se pudo grabar {window.Executable}, así que se graba la pantalla.", ToolTipIcon.Warning);
            await StartReplayAsync(force: false);
            return;
        }
        if (!retriedAfterFailure)
        {
            // puede ser un driver actualizado o un cambio de gpu
            retriedAfterFailure = true;
            Notify("Se cortó la grabación, reintentando…", ToolTipIcon.Warning);
            await StartReplayAsync(force: true);
        }
        else
        {
            SetStatus("La grabación se detuvo por un error", idleIcon);
            Notify($"La grabación se detuvo: {error}", ToolTipIcon.Error);
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
            Notify("La repetición está apagada. Activala desde el ícono de la bandeja.", ToolTipIcon.Warning);
            return;
        }
        if (saving)
        {
            Notify("Esperá, todavía se está guardando el clip anterior.", ToolTipIcon.Info);
            return;
        }
        saving = true;
        tray.Icon = busyIcon;
        try
        {
            var (path, v, s) = (ffmpeg, video, settings.Clone());
            var (sys, mic) = (systemAudio?.Ring, micAudio?.Ring);
            var clip = await Task.Run(() => ClipSaver.SaveAsync(path, v, sys, mic, s));
            SystemSounds.Asterisk.Play();
            Notify("Clip guardado. Hacé clic acá para recortarlo.", ToolTipIcon.Info, () => OpenEditor(clip));
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
            using var form = new SettingsForm(settings.Clone(), GpuInfo.Enumerate(), pipeline?.Description, AudioRecorder.ListMicrophones(), PreviewFor, PickClipToEdit);
            settingsForm = form;
            if (form.ShowDialog() != DialogResult.OK) return;

            var updated = form.Result;
            bool restart = IsRunning && (form.ForceRedetect || updated.CaptureChanged(settings));
            if (form.ForceRedetect) updated.CachedPipelineId = null;
            failedWindow = IntPtr.Zero;
            settings = updated;
            settings.Save();
            settings.ApplyStartWithWindows();
            StartUpdateChecks();
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

    void StartUpdateChecks()
    {
        updateTimer.Stop();
        if (!settings.CheckForUpdates || !Updater.CanSelfUpdate) return;
        updateTimer.Interval = (int)FirstUpdateCheck.TotalMilliseconds;
        updateTimer.Start();
    }

    async Task OnUpdateTimerAsync()
    {
        updateTimer.Interval = (int)UpdateCheckInterval.TotalMilliseconds;
        await CheckForUpdatesAsync(manual: false);
    }

    async Task OnUpdateItemAsync()
    {
        if (availableUpdate != null) await ApplyUpdateAsync();
        else await CheckForUpdatesAsync(manual: true);
    }

    async Task CheckForUpdatesAsync(bool manual)
    {
        if (!Updater.CanSelfUpdate)
        {
            if (manual) Notify("Las actualizaciones solo funcionan con el Cliperino.exe publicado.", ToolTipIcon.Info);
            return;
        }
        try
        {
            availableUpdate = await Updater.CheckAsync(Updater.CurrentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write($"No se pudo buscar actualizaciones: {ex.Message}");
            if (manual) Notify("No se pudo buscar actualizaciones. Revisá tu conexión a internet.", ToolTipIcon.Warning);
            return;
        }

        updateItem.Text = availableUpdate != null ? $"Actualizar a {availableUpdate.Tag}…" : "Buscar actualizaciones";
        if (availableUpdate != null && (manual || availableUpdate.Version != notifiedVersion))
        {
            notifiedVersion = availableUpdate.Version;
            Notify($"Salió Cliperino {availableUpdate.Tag}. Hacé clic acá para actualizar.", ToolTipIcon.Info, () => _ = ApplyUpdateAsync());
        }
        else if (availableUpdate == null && manual)
        {
            Notify($"Ya tenés la última versión ({Updater.CurrentVersion.ToString(3)}).", ToolTipIcon.Info);
        }
    }

    async Task ApplyUpdateAsync()
    {
        var update = availableUpdate;
        if (update == null || updating) return;
        if (saving)
        {
            Notify("Esperá a que termine de guardarse el clip.", ToolTipIcon.Info);
            return;
        }
        var answer = MessageBox.Show(
            $"¿Actualizar Cliperino a la versión {update.Tag}?\n\nCliperino se reinicia y se pierde lo grabado que no hayas guardado como clip.",
            "Cliperino", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        updating = true;
        using var window = new DownloadForm($"Cliperino – Actualizando a {update.Tag}");
        var progress = new Progress<(string Text, int? Percent)>(p => window.Report(p.Text, p.Percent));
        window.Show();
        try
        {
            await Updater.InstallAndRestartAsync(update, progress, window.Cancellation);
            Log.Write($"Actualizando a {update.Tag}.");
            ExitApp();
        }
        catch (OperationCanceledException)
        {
            Log.Write("Actualización cancelada.");
        }
        catch (Exception ex)
        {
            Log.Write($"No se pudo actualizar: {ex}");
            MessageBox.Show($"No se pudo actualizar: {ex.Message}\n\nPodés descargar la versión nueva desde {update.PageUrl}",
                "Cliperino", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            updating = false;
            window.Close();
        }
    }

    void PickClipToEdit(IWin32Window? owner = null)
    {
        Directory.CreateDirectory(settings.OutputFolder);
        using var dialog = new OpenFileDialog
        {
            InitialDirectory = settings.OutputFolder,
            Filter = "Clips (*.mp4)|*.mp4",
            Title = "Elegí el clip a editar",
        };
        if (dialog.ShowDialog(owner) == DialogResult.OK) OpenEditor(dialog.FileName);
    }

    void OpenEditor(string clip)
    {
        if (ffmpeg == null || !File.Exists(clip)) return;
        new EditorForm(ffmpeg, clip, PipelineBuilder.FileEncoderArgs(settings.CachedPipelineId, settings.BitrateMbps)).Show();
    }

    PreviewSource PreviewFor(CaptureChoice choice)
    {
        if (ffmpeg is not { } path || caps == null || choice.Monitor is not { } monitor)
            return new PreviewSource(null, "", "Falta ffmpeg para ver la vista previa.");

        var window = choice.Mode == CaptureMode.Window && caps.HasGfxcapture && caps.HasDdagrab ? GameWindow.Find(choice.Games) : null;
        if (window?.Handle == failedWindow) window = null;
        var what = window?.Executable ?? "la pantalla";
        bool recordingThis = video != null && window?.Handle == captureWindow?.Handle &&
            (window != null || string.Equals(pipeline?.Monitor?.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (recordingThis)
            return new PreviewSource(() => LivePreview.FromBuffer(path, () => video?.ListSegments() ?? []), "buffer", $"Grabando {what}");

        var ddagrab = caps.HasDdagrab;
        var handle = window?.Handle ?? IntPtr.Zero;
        var caption = video != null ? "Así se va a ver cuando guardes." : "La repetición está apagada. Así se vería al activarla.";
        return new PreviewSource(() => LivePreview.FromCapture(path, monitor, handle, ddagrab), $"{monitor.DeviceName}|{handle}", caption);
    }

    void RegisterHotkey()
    {
        if (hotkey.Register(settings.Hotkey)) Log.Write($"Atajo registrado: {settings.Hotkey}");
        else
        {
            Log.Write($"No se pudo registrar el atajo {settings.Hotkey}.");
            Notify($"El atajo {settings.Hotkey} ya lo usa otra app. Elegí otro en Ajustes.", ToolTipIcon.Warning);
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
        updateTimer.Dispose();
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

    void Notify(string text, ToolTipIcon icon, Action? onClick = null)
    {
        balloonClick = onClick;
        tray.ShowBalloonTip(4000, "Cliperino", text.Length > 250 ? text[..250] : text, icon);
    }

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
