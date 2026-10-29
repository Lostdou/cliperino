using static Cliperino.Core.Lang;
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
    readonly OverlayToast overlay = new();
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
    bool starting, saving, retriedAfterFailure, warnedNoHevc;
    Action? balloonClick;

    bool IsRunning => video != null;

    public TrayApp()
    {
        statusItem = new ToolStripMenuItem(T("Iniciando…", "Starting…")) { Enabled = false };
        pipelineItem = new ToolStripMenuItem("") { Enabled = false, Visible = false };
        toggleItem = new ToolStripMenuItem(T("Activar repetición instantánea", "Turn on instant replay"), null, async (_, _) => await ToggleAsync());
        saveItem = new ToolStripMenuItem(T("Guardar clip", "Save clip"), null, async (_, _) => await SaveClipAsync()) { Enabled = false };
        updateItem = new ToolStripMenuItem(T("Buscar actualizaciones", "Check for updates"), null, async (_, _) => await OnUpdateItemAsync());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem, pipelineItem, new ToolStripSeparator(),
            toggleItem, saveItem,
            new ToolStripMenuItem(T("Editar clip…", "Edit clip…"), null, (_, _) => PickClipToEdit()),
            new ToolStripMenuItem(T("Abrir carpeta de clips", "Open clips folder"), null, (_, _) => OpenClipsFolder()),
            new ToolStripMenuItem(T("Ajustes…", "Settings…"), null, (_, _) => ShowSettings()),
            updateItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem(T("Salir", "Exit"), null, (_, _) => ExitApp()),
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
                    T("Para grabar, Cliperino necesita ffmpeg y no está instalado.\n\n¿Descargarlo ahora? Son unos 100 MB.", "Cliperino needs ffmpeg to record and it isn't installed.\n\nDownload it now? It's about 100 MB."),
                    "Cliperino", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    SetStatus(T("ffmpeg no encontrado", "ffmpeg not found"), idleIcon);
                    return;
                }
                ffmpeg = await DownloadFfmpegAsync();
                if (ffmpeg == null)
                {
                    SetStatus(T("ffmpeg no encontrado", "ffmpeg not found"), idleIcon);
                    return;
                }
            }
            caps = await FfmpegLocator.InspectAsync(ffmpeg);
            Log.Write($"ffmpeg: {ffmpeg} - {caps}");
            if (!caps.HasDdagrab) Log.Write("Este ffmpeg no tiene ddagrab (requiere ffmpeg 6+); sólo se podrá usar gdigrab.");

            if (settings.StartReplayOnLaunch) await StartReplayAsync(force: false);
            else SetStatus(T("Repetición desactivada", "Instant replay off"), idleIcon);
            if (Updater.JustUpdated) Notify(T($"Cliperino se actualizó a la versión {Updater.CurrentVersion.ToString(3)}.", $"Cliperino was updated to version {Updater.CurrentVersion.ToString(3)}."), ToolTipIcon.Info);
            StartUpdateChecks();
        }
        catch (Exception ex)
        {
            Log.Write($"Error al iniciar: {ex}");
            SetStatus(T("No se pudo iniciar", "Couldn't start"), idleIcon);
            Notify(T($"Cliperino no pudo iniciar: {ex.Message}", $"Cliperino couldn't start: {ex.Message}"), ToolTipIcon.Error);
        }
    }

    async Task<string?> DownloadFfmpegAsync()
    {
        using var window = new DownloadForm(T("Cliperino - Descargando ffmpeg", "Cliperino - Downloading ffmpeg"));
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
            SetStatus(T("Preparando la grabación…", "Getting ready to record…"), busyIcon);
            var (path, capabilities, current) = (ffmpeg, caps, settings);
            var progress = new Progress<string>(msg => SetStatus(msg, busyIcon));
            var resolved = await Task.Run(() => PipelineBuilder.ResolveAsync(path, capabilities, current, force, progress));
            if (resolved == null)
            {
                SetStatus(T("No se pudo empezar a grabar", "Couldn't start recording"), idleIcon);
                Notify(T(@"No se pudo empezar a grabar. Los detalles quedaron en %LOCALAPPDATA%\Cliperino\logs\probe.log", @"Couldn't start recording. Details are in %LOCALAPPDATA%\Cliperino\logs\probe.log"), ToolTipIcon.Error);
                return;
            }
            settings.Save(); // persiste la cache del pipeline
            pipeline = resolved;
            if (current.Codec == VideoCodec.Hevc && resolved.Codec != VideoCodec.Hevc && !warnedNoHevc)
            {
                warnedNoHevc = true;
                Notify(T("Tu placa no puede grabar en HEVC, así que se graba en H.264.", "Your graphics card can't record HEVC, so it records H.264 instead."), ToolTipIcon.Warning);
            }
            var window = captureWindow = FindGameWindow();

            // wasapi da problemas si se inicializa en el hilo sta de la ui
            var bufferSeconds = settings.ReplaySeconds + 10;
            await Task.Run(async () =>
            {
                if (current.CaptureSystemAudio) systemAudio = await CreateSystemAudioAsync(current, window, bufferSeconds);
                if (current.CaptureMicrophone) micAudio = new AudioRecorder(DataFlow.Capture, current.MicrophoneDeviceId, bufferSeconds);
            });

            var args = PipelineBuilder.BuildArgs(resolved.Id, resolved.Codec, resolved.Monitor, current.Fps, current.BitrateMbps, window?.Handle ?? IntPtr.Zero);
            video = new VideoBuffer(path, args, current.ReplaySeconds);
            video.Failed += OnVideoFailed;
            video.Start();
            var what = window != null ? current.DisplayNameOf(window.Executable) : T("la pantalla", "the screen");
            SetStatus(T($"Grabando {what}: últimos {FormatDuration(current.ReplaySeconds)}", $"Recording {what}: last {FormatDuration(current.ReplaySeconds)}"), recordingIcon);
            // windows 11 esconde los iconos nuevos de la bandeja, sin aviso no se sabe que esta grabando
            Feedback(T("Repetición instantánea activada", "Instant replay on"),
                T($"{what} · {current.Hotkey} guarda un clip", $"{what} · {current.Hotkey} saves a clip"),
                () => Notify(T($"Grabando {what}. Apretá {current.Hotkey} para guardar los últimos {FormatDuration(current.ReplaySeconds)}.", $"Recording {what}. Press {current.Hotkey} to save the last {FormatDuration(current.ReplaySeconds)}."), ToolTipIcon.Info));
        }
        catch (Exception ex)
        {
            Log.Write($"Error al iniciar la captura: {ex}");
            StopReplay();
            SetStatus(T("No se pudo empezar a grabar", "Couldn't start recording"), idleIcon);
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
        SetStatus(T("Repetición desactivada", "Instant replay off"), idleIcon);
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
            var name = settings.DisplayNameOf(window.Executable);
            Notify(T($"No se pudo grabar {name}, así que se graba la pantalla.", $"Couldn't record {name}, recording the screen instead."), ToolTipIcon.Warning);
            await StartReplayAsync(force: false);
            return;
        }
        if (!retriedAfterFailure)
        {
            // puede ser un driver actualizado o un cambio de gpu
            retriedAfterFailure = true;
            Notify(T("Se cortó la grabación, reintentando…", "Recording stopped, retrying…"), ToolTipIcon.Warning);
            await StartReplayAsync(force: true);
        }
        else
        {
            SetStatus(T("La grabación se detuvo por un error", "Recording stopped because of an error"), idleIcon);
            Notify(T($"La grabación se detuvo: {error}", $"Recording stopped: {error}"), ToolTipIcon.Error);
        }
    }, null);

    async Task ToggleAsync()
    {
        retriedAfterFailure = false;
        failedWindow = IntPtr.Zero;
        if (IsRunning)
        {
            StopReplay();
            var off = T("Repetición instantánea desactivada", "Instant replay off");
            Feedback(off, null, () => Notify(off, ToolTipIcon.Info));
        }
        else await StartReplayAsync(force: false);
    }

    async Task SaveClipAsync()
    {
        if (ffmpeg == null || video == null)
        {
            Notify(T("La repetición está apagada. Activala desde el ícono de la bandeja.", "Instant replay is off. Turn it on from the tray icon."), ToolTipIcon.Warning);
            return;
        }
        if (saving)
        {
            Notify(T("Esperá, todavía se está guardando el clip anterior.", "Hold on, the previous clip is still being saved."), ToolTipIcon.Info);
            return;
        }
        saving = true;
        tray.Icon = busyIcon;
        try
        {
            var (path, v, s) = (ffmpeg, video, settings.Clone());
            var game = s.FolderPerGame ? captureWindow ?? GameWindow.Find(s.GameExecutables) : null;
            if (game != null && GameWindow.FolderName(game.Executable, s.AliasOf(game.Executable)) is { } folder) s.OutputFolder = Path.Combine(s.OutputFolder, folder);
            var (sys, mic) = (systemAudio?.Ring, micAudio?.Ring);
            var codec = pipeline?.Codec ?? VideoCodec.H264;
            var clip = await Task.Run(() => ClipSaver.SaveAsync(path, v, sys, mic, s, codec));
            Feedback($"{T("Clip guardado", "Clip saved")} · {FormatDuration((int)Math.Round(clip.Duration.TotalSeconds))}", null, () =>
            {
                SystemSounds.Asterisk.Play();
                Notify(T("Clip guardado. Hacé clic acá para recortarlo.", "Clip saved. Click here to trim it."), ToolTipIcon.Info, () => OpenEditor(clip.Path));
            });
        }
        catch (Exception ex)
        {
            Log.Write($"Error guardando el clip: {ex}");
            Notify(T($"No se pudo guardar el clip: {ex.Message}", $"Couldn't save the clip: {ex.Message}"), ToolTipIcon.Error);
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
            warnedNoHevc = false;
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
            if (manual) Notify(T("Las actualizaciones solo funcionan con el Cliperino.exe publicado.", "Updates only work with the published Cliperino.exe."), ToolTipIcon.Info);
            return;
        }
        try
        {
            availableUpdate = await Updater.CheckAsync(Updater.CurrentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write($"No se pudo buscar actualizaciones: {ex.Message}");
            if (manual) Notify(T("No se pudo buscar actualizaciones. Revisá tu conexión a internet.", "Couldn't check for updates. Check your internet connection."), ToolTipIcon.Warning);
            return;
        }

        updateItem.Text = availableUpdate != null ? T($"Actualizar a {availableUpdate.Tag}…", $"Update to {availableUpdate.Tag}…") : T("Buscar actualizaciones", "Check for updates");
        if (availableUpdate != null && (manual || availableUpdate.Version != notifiedVersion))
        {
            notifiedVersion = availableUpdate.Version;
            Notify(T($"Salió Cliperino {availableUpdate.Tag}. Hacé clic acá para actualizar.", $"Cliperino {availableUpdate.Tag} is out. Click here to update."), ToolTipIcon.Info, () => _ = ApplyUpdateAsync());
        }
        else if (availableUpdate == null && manual)
        {
            Notify(T($"Ya tenés la última versión ({Updater.CurrentVersion.ToString(3)}).", $"You have the latest version ({Updater.CurrentVersion.ToString(3)})."), ToolTipIcon.Info);
        }
    }

    async Task ApplyUpdateAsync()
    {
        var update = availableUpdate;
        if (update == null || updating) return;
        if (saving)
        {
            Notify(T("Esperá a que termine de guardarse el clip.", "Wait for the clip to finish saving."), ToolTipIcon.Info);
            return;
        }
        var answer = MessageBox.Show(
            T($"¿Actualizar Cliperino a la versión {update.Tag}?\n\nCliperino se reinicia y se pierde lo grabado que no hayas guardado como clip.", $"Update Cliperino to version {update.Tag}?\n\nCliperino will restart and anything you haven't saved as a clip will be lost."),
            "Cliperino", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        updating = true;
        using var window = new DownloadForm(T($"Cliperino - Actualizando a {update.Tag}", $"Cliperino - Updating to {update.Tag}"));
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
            MessageBox.Show(T($"No se pudo actualizar: {ex.Message}\n\nPodés descargar la versión nueva desde {update.PageUrl}", $"Couldn't update: {ex.Message}\n\nYou can download the new version from {update.PageUrl}"),
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
            Title = T("Elegí el clip a editar", "Choose the clip to edit"),
        };
        if (dialog.ShowDialog(owner) == DialogResult.OK) OpenEditor(dialog.FileName);
    }

    void OpenEditor(string clip)
    {
        if (ffmpeg == null || !File.Exists(clip)) return;
        new EditorForm(ffmpeg, clip, codec => PipelineBuilder.FileEncoderArgs(settings.CachedPipelineId, codec, settings.BitrateMbps)).Show();
    }

    PreviewSource PreviewFor(CaptureChoice choice)
    {
        if (ffmpeg is not { } path || caps == null || choice.Monitor is not { } monitor)
            return new PreviewSource(null, "", T("Falta ffmpeg para ver la vista previa.", "The preview needs ffmpeg."));

        var window = choice.Mode == CaptureMode.Window && caps.HasGfxcapture && caps.HasDdagrab ? GameWindow.Find(choice.Games) : null;
        if (window?.Handle == failedWindow) window = null;
        var what = window != null ? choice.Aliases.GetValueOrDefault(window.Executable) ?? window.Executable : T("la pantalla", "the screen");
        bool recordingThis = video != null && window?.Handle == captureWindow?.Handle &&
            (window != null || string.Equals(pipeline?.Monitor?.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (recordingThis)
            return new PreviewSource(() => LivePreview.FromBuffer(path, () => video?.ListSegments() ?? []), "buffer", T($"Grabando {what}", $"Recording {what}"));

        var ddagrab = caps.HasDdagrab;
        var handle = window?.Handle ?? IntPtr.Zero;
        var caption = video != null ? T("Así se va a ver cuando guardes.", "This is how it will look once you save.") : T("La repetición está apagada. Así se vería al activarla.", "Instant replay is off. This is how it would look.");
        return new PreviewSource(() => LivePreview.FromCapture(path, monitor, handle, ddagrab), $"{monitor.DeviceName}|{handle}", caption);
    }

    void RegisterHotkey()
    {
        if (hotkey.Register(settings.Hotkey)) Log.Write($"Atajo registrado: {settings.Hotkey}");
        else
        {
            Log.Write($"No se pudo registrar el atajo {settings.Hotkey}.");
            Notify(T($"El atajo {settings.Hotkey} ya lo usa otra app. Elegí otro en Ajustes.", $"Another app is already using {settings.Hotkey}. Choose a different one in Settings."), ToolTipIcon.Warning);
        }
        saveItem.Text = T($"Guardar clip ({settings.Hotkey})", $"Save clip ({settings.Hotkey})");
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
        overlay.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    void SetStatus(string text, Icon icon)
    {
        statusItem.Text = text;
        pipelineItem.Text = captureWindow != null ? T($"{pipeline?.Description} · ventana de {settings.DisplayNameOf(captureWindow.Executable)}", $"{pipeline?.Description} · {settings.DisplayNameOf(captureWindow.Executable)} window") : pipeline?.Description ?? "";
        pipelineItem.Visible = pipeline != null;
        var tip = $"Cliperino - {text}";
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
        tray.Icon = icon;
    }

    void UpdateMenu()
    {
        toggleItem.Text = IsRunning ? T("Desactivar repetición instantánea", "Turn off instant replay") : T("Activar repetición instantánea", "Turn on instant replay");
        saveItem.Enabled = IsRunning;
    }

    void Feedback(string title, string? detail, Action sound)
    {
        if (settings.Feedback == FeedbackStyle.Overlay) overlay.Show(title, detail);
        else sound();
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
