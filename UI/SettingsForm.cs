using static Cliperino.Core.Lang;
using System.Diagnostics;
using Cliperino.Capture;
using Cliperino.Core;
using Cliperino.Update;

namespace Cliperino.UI;

public sealed record CaptureChoice(CaptureMode Mode, MonitorInfo? Monitor, IReadOnlyList<string> Games);

public sealed record PreviewSource(Func<LivePreview>? Create, string Key, string Caption);

sealed class SettingsForm : Form
{
    sealed record Item(string Text, object? Value)
    {
        public override string ToString() => Text;
    }

    const string RepoUrl = "https://github.com/Lostdou/cliperino";
    const string TwitterUrl = "https://x.com/nosoylostdou";
    static readonly Size PreviewSize = new(480, 270);

    readonly AppSettings s;
    readonly Func<CaptureChoice, PreviewSource> previewSource;
    readonly TabControl tabs = new();
    readonly PictureBox preview = new() { Size = PreviewSize, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly Label previewCaption = new() { AutoSize = true, MaximumSize = new Size(PreviewSize.Width, 0), ForeColor = SystemColors.GrayText };
    readonly System.Windows.Forms.Timer captionTimer = new() { Interval = 1000 };
    LivePreview? livePreview;
    string? previewKey;
    int framePending;

    readonly NumericUpDown seconds = Number(5, 1200, 5);
    readonly Label secondsHint = Hint("");
    readonly ComboBox captureMode = Dropdown(380);
    readonly ListBox games = new() { Width = 300, Height = 64, IntegralHeight = false };
    readonly Button addGame = new() { Text = T("Agregar…", "Add…"), AutoSize = true };
    readonly Button removeGame = new() { Text = T("Quitar", "Remove"), AutoSize = true };
    readonly ComboBox monitor = Dropdown(380);
    readonly ComboBox fps = Dropdown(90);
    readonly CheckBox systemAudio = Check(T("Sistema:", "System:"));
    readonly ComboBox audioSource = Dropdown(220);
    readonly CheckBox mic = Check(T("Micrófono:", "Microphone:"));
    readonly ComboBox micDevice = Dropdown(300);
    readonly TextBox hotkeyBox = new() { ReadOnly = true, Width = 160, BackColor = SystemColors.Window };
    readonly TextBox folder = new() { Width = 340 };

    readonly ComboBox encoder = Dropdown(380);
    readonly Label detected = Hint("");
    readonly NumericUpDown bitrate = Number(2, 150, 1);
    readonly NumericUpDown offset = Number(-2000, 2000, 10);

    readonly CheckBox startWithWindows = Check(T("Iniciar con Windows", "Start with Windows"));
    readonly CheckBox startOnLaunch = Check(T("Empezar a grabar al abrir Cliperino", "Start recording when Cliperino opens"));
    readonly ComboBox language = Dropdown(200);
    readonly CheckBox checkUpdates = Check(T("Avisarme cuando haya una versión nueva", "Let me know when there's a new version"));

    public AppSettings Result => s;
    public bool ForceRedetect { get; private set; }

    public SettingsForm(AppSettings settings, GpuSnapshot gpus, string? activePipeline,
        IReadOnlyList<(string Id, string Name)> microphones, Func<CaptureChoice, PreviewSource> previewSource, Action<IWin32Window> editClip)
    {
        s = settings;
        this.previewSource = previewSource;
        Text = T("Cliperino – Ajustes", "Cliperino – Settings");
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        LoadCaptureValues(gpus, microphones);
        LoadAdvancedValues(gpus, activePipeline);
        startWithWindows.Checked = s.StartWithWindows;
        startOnLaunch.Checked = s.StartReplayOnLaunch;
        checkUpdates.Checked = s.CheckForUpdates;
        language.Items.Add(new Item(T("Automático (el de Windows)", "Automatic (same as Windows)"), Language.Auto));
        language.Items.Add(new Item("Español", Language.Spanish));
        language.Items.Add(new Item("English", Language.English));
        language.SelectedItem = language.Items.Cast<Item>().First(i => Equals(i.Value, s.Language));

        var pages = new[] { CaptureTab(), AdvancedTab(), ExtrasTab() };
        tabs.TabPages.AddRange(pages);
        tabs.SelectedIndexChanged += (_, _) => UpdatePreview();
        captureMode.SelectedIndexChanged += (_, _) => UpdatePreview();
        monitor.SelectedIndexChanged += (_, _) => UpdatePreview();

        var save = new Button { Text = T("Guardar", "Save"), AutoSize = true };
        save.Click += OnSave;
        var cancel = new Button { Text = T("Cancelar", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = Padding.Empty,
        };
        buttons.Controls.AddRange(new Control[] { cancel, save });

        var edit = new Button { Text = T("Editar clip…", "Edit clip…"), AutoSize = true, Anchor = AnchorStyles.Left };
        edit.Click += (_, _) => editClip(this);
        var footer = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Margin = Padding.Empty,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(edit);
        footer.Controls.Add(buttons);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        root.Controls.Add(tabs);
        root.Controls.Add(footer);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        Load += (_, _) => FitTabs(pages);
        Shown += (_, _) =>
        {
            Activate();
            captionTimer.Start();
            UpdatePreview();
        };
        captionTimer.Tick += (_, _) => UpdatePreview();
        FormClosing += (_, _) =>
        {
            captionTimer.Stop();
            StopPreview();
        };
    }

    void LoadCaptureValues(GpuSnapshot gpus, IReadOnlyList<(string Id, string Name)> microphones)
    {
        seconds.Value = s.ReplaySeconds;
        seconds.ValueChanged += (_, _) => secondsHint.Text = "= " + TrayApp.FormatDuration((int)seconds.Value);
        secondsHint.Text = "= " + TrayApp.FormatDuration(s.ReplaySeconds);

        captureMode.Items.Add(new Item(T("Juego: solo la ventana del juego (recomendado)", "Game: just the game window (recommended)"), CaptureMode.Window));
        captureMode.Items.Add(new Item(T("Pantalla: todo lo que se ve en el monitor", "Screen: everything on the monitor"), CaptureMode.Monitor));
        captureMode.SelectedItem = captureMode.Items.Cast<Item>().First(i => Equals(i.Value, s.CaptureMode));

        games.Items.AddRange(s.GameExecutables.Cast<object>().ToArray());
        addGame.Click += (_, _) => ShowOpenWindows(addGame);
        removeGame.Click += (_, _) =>
        {
            if (games.SelectedItem != null) games.Items.Remove(games.SelectedItem);
            UpdatePreview();
        };

        foreach (var m in gpus.Monitors) monitor.Items.Add(new Item(m.Label, m));
        var currentMonitor = gpus.FindMonitor(s.MonitorDeviceName);
        monitor.SelectedItem = monitor.Items.Cast<Item>().FirstOrDefault(i => ReferenceEquals(i.Value, currentMonitor));

        fps.Items.AddRange(new object[] { 30, 60 });
        fps.SelectedItem = s.Fps;

        systemAudio.Checked = s.CaptureSystemAudio;
        audioSource.Items.Add(new Item(T("Todo lo que suena en la PC", "Everything playing on the PC"), AudioSource.Desktop));
        audioSource.Items.Add(new Item(T("Solo el juego (Windows 11)", "Just the game (Windows 11)"), AudioSource.Game));
        audioSource.SelectedItem = audioSource.Items.Cast<Item>().First(i => Equals(i.Value, s.AudioSource));

        mic.Checked = s.CaptureMicrophone;
        micDevice.Items.Add(new Item(T("Predeterminado de Windows", "Windows default"), null));
        foreach (var (id, name) in microphones) micDevice.Items.Add(new Item(name, id));
        micDevice.SelectedItem = micDevice.Items.Cast<Item>().FirstOrDefault(i => Equals(i.Value, s.MicrophoneDeviceId)) ?? micDevice.Items[0];

        void UpdateEnabled()
        {
            bool window = Equals((captureMode.SelectedItem as Item)?.Value, CaptureMode.Window);
            games.Enabled = addGame.Enabled = removeGame.Enabled = window;
            audioSource.Enabled = window && systemAudio.Checked;
            micDevice.Enabled = mic.Checked;
        }
        captureMode.SelectedIndexChanged += (_, _) => UpdateEnabled();
        systemAudio.CheckedChanged += (_, _) => UpdateEnabled();
        mic.CheckedChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();

        hotkeyBox.Text = s.Hotkey;
        hotkeyBox.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            if (HotkeyWindow.Format(e.KeyData) is { } text) hotkeyBox.Text = text;
        };

        folder.Text = s.OutputFolder;
    }

    void LoadAdvancedValues(GpuSnapshot gpus, string? activePipeline)
    {
        var vendors = gpus.Adapters.Select(a => a.Vendor).ToHashSet();
        encoder.Items.Add(new Item(T("Automático (recomendado)", "Automatic (recommended)"), EncoderPreference.Auto));
        if (vendors.Contains(GpuVendor.Nvidia)) encoder.Items.Add(new Item("NVIDIA (NVENC)", EncoderPreference.Nvenc));
        if (vendors.Contains(GpuVendor.Amd)) encoder.Items.Add(new Item("AMD (AMF)", EncoderPreference.Amf));
        if (vendors.Contains(GpuVendor.Intel)) encoder.Items.Add(new Item("Intel (Quick Sync)", EncoderPreference.Qsv));
        encoder.Items.Add(new Item(T("Procesador (x264, exige más a la PC)", "Processor (x264, heavier on the PC)"), EncoderPreference.X264));
        encoder.SelectedItem = encoder.Items.Cast<Item>().FirstOrDefault(i => Equals(i.Value, s.Encoder)) ?? encoder.Items[0];

        detected.Text = activePipeline ?? T("Se elige al empezar a grabar", "Chosen when recording starts");
        bitrate.Value = s.BitrateMbps;
        offset.Value = s.AudioOffsetMs;
    }

    TabPage CaptureTab()
    {
        var browse = new Button { Text = "…", Width = 32 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                SelectedPath = folder.Text,
                UseDescriptionForTitle = true,
                Description = T("Carpeta donde se guardan los clips", "Folder where clips are saved"),
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        };

        var grid = Grid();
        Row(grid, T("Guardar los últimos (seg):", "Save the last (sec):"), seconds, secondsHint);
        Row(grid, T("Captura:", "Capture:"), captureMode);
        Row(grid, T("Juegos:", "Games:"), games, Stack(addGame, removeGame));
        Row(grid, "", Hint(T("Abrí el juego y elegilo con Agregar…", "Open the game and pick it with Add…")));
        Row(grid, "Monitor:", monitor);
        Row(grid, "FPS:", fps);
        Row(grid, "Audio:", systemAudio, audioSource);
        Row(grid, "", mic, micDevice);
        Row(grid, T("Atajo para guardar:", "Save shortcut:"), hotkeyBox, Hint(T("Hacé clic acá y apretá las teclas", "Click here and press the keys")));
        Row(grid, T("Carpeta de clips:", "Clips folder:"), folder, browse);

        var previewPanel = Stack(new Label { Text = T("Vista previa en vivo", "Live preview"), AutoSize = true }, preview, previewCaption);
        previewPanel.Margin = new Padding(16, 0, 0, 0);
        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        layout.Controls.Add(grid);
        layout.Controls.Add(previewPanel);
        return Page(T("Captura", "Capture"), layout);
    }

    TabPage AdvancedTab()
    {
        var redetect = new Button { Text = T("Volver a detectar", "Detect again"), AutoSize = true };
        redetect.Click += (_, _) =>
        {
            ForceRedetect = true;
            detected.Text = T("Se vuelve a elegir al guardar", "It will be chosen again when you save");
        };

        var grid = Grid();
        Row(grid, T("Codificador:", "Encoder:"), encoder);
        Row(grid, T("En uso:", "In use:"), detected, redetect);
        Row(grid, "Bitrate (Mbps):", bitrate);
        Row(grid, T("Desfase de audio (ms):", "Audio offset (ms):"), offset, Hint(T("Subilo si el sonido llega antes que la imagen", "Raise it if the sound comes before the picture")));
        return Page(T("Avanzado", "Advanced"), grid);
    }

    TabPage ExtrasTab()
    {
        var grid = Grid();
        Row(grid, T("Inicio:", "Startup:"), startWithWindows);
        Row(grid, "", startOnLaunch);
        Row(grid, T("Actualizaciones:", "Updates:"), checkUpdates);
        Row(grid, T("Idioma:", "Language:"), language, Hint(T("Se aplica al reiniciar Cliperino", "Applies after restarting Cliperino")));
        Row(grid, T("Versión:", "Version:"), new Label { Text = Updater.CurrentVersion.ToString(3), AutoSize = true, Anchor = AnchorStyles.Left });
        Row(grid, T("Proyecto:", "Project:"), Link(T("Mirá el proyecto en GitHub", "Check out the project on GitHub"), T("Abrir GitHub", "Open GitHub"), RepoUrl));
        Row(grid, "", Link(T("¿Encontraste un bug o tenés una idea?", "Found a bug or have an idea?"), T("Contarlo", "Tell me"), RepoUrl + "/issues"));
        Row(grid, "", Link(T("Seguime en Twitter", "Follow me on Twitter"), "@nosoylostdou", TwitterUrl));
        return Page("Extras", grid);
    }

    static Control Link(string text, string button, string url)
    {
        var open = new Button { Text = button, AutoSize = true };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(260, 0) };
        return Flow([label, open]);
    }

    void FitTabs(TabPage[] pages)
    {
        var content = new Size(
            pages.Max(p => p.Controls[0].PreferredSize.Width),
            pages.Max(p => p.Controls[0].PreferredSize.Height));
        var chrome = tabs.Size - tabs.DisplayRectangle.Size;
        tabs.Size = content + chrome + pages[0].Padding.Size;
    }

    void UpdatePreview()
    {
        var source = previewSource(new CaptureChoice(
            (CaptureMode)((Item)captureMode.SelectedItem!).Value!,
            (monitor.SelectedItem as Item)?.Value as MonitorInfo,
            games.Items.Cast<string>().ToList()));
        previewCaption.Text = source.Caption;
        if (tabs.SelectedIndex == 0 && source.Create != null) StartPreview(source);
        else StopPreview();
    }

    void StartPreview(PreviewSource source)
    {
        if (source.Key != previewKey)
        {
            StopPreview();
            var old = preview.Image;
            preview.Image = null;
            old?.Dispose();
            previewKey = source.Key;
        }
        if (livePreview != null) return;
        livePreview = source.Create!();
        livePreview.FrameReady += ShowFrame;
        livePreview.Start();
    }

    void StopPreview()
    {
        if (livePreview == null) return;
        livePreview.FrameReady -= ShowFrame;
        livePreview.Dispose();
        livePreview = null;
    }

    void ShowFrame(Bitmap frame)
    {
        if (Interlocked.Exchange(ref framePending, 1) == 1 || IsDisposed || !IsHandleCreated)
        {
            frame.Dispose();
            return;
        }
        try
        {
            BeginInvoke(() =>
            {
                var previous = preview.Image;
                preview.Image = frame;
                previous?.Dispose();
                framePending = 0;
            });
        }
        catch (InvalidOperationException)
        {
            frame.Dispose();
        }
    }

    void ShowOpenWindows(Control anchor)
    {
        var menu = new ContextMenuStrip();
        foreach (var window in GameWindow.ListOpen())
        {
            var item = new ToolStripMenuItem($"{window.Title}   ({window.Executable})", IconOf(window.Path));
            item.Click += (_, _) => AddGame(window.Executable);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("Buscar ejecutable…", "Browse for executable…"), null, (_, _) => BrowseGame());
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    void BrowseGame()
    {
        using var dialog = new OpenFileDialog { Filter = T("Ejecutables (*.exe)|*.exe", "Executables (*.exe)|*.exe"), Title = T("Elegí el ejecutable del juego", "Choose the game's executable") };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddGame(Path.GetFileName(dialog.FileName));
    }

    void AddGame(string executable)
    {
        if (!games.Items.Cast<string>().Contains(executable, StringComparer.OrdinalIgnoreCase)) games.Items.Add(executable);
        games.SelectedItem = games.Items.Cast<string>().First(g => string.Equals(g, executable, StringComparison.OrdinalIgnoreCase));
        UpdatePreview();
    }

    void OnSave(object? sender, EventArgs e)
    {
        if (!HotkeyWindow.TryParse(hotkeyBox.Text, out _, out _))
        {
            MessageBox.Show(this, T("Ese atajo no sirve, probá con otra combinación.", "That shortcut doesn't work, try another combination."), "Cliperino", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(folder.Text))
        {
            MessageBox.Show(this, T("Elegí una carpeta para los clips.", "Choose a folder for your clips."), "Cliperino", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        s.ReplaySeconds = (int)seconds.Value;
        s.CaptureMode = (CaptureMode)((Item)captureMode.SelectedItem!).Value!;
        s.GameExecutables = games.Items.Cast<string>().ToList();
        if ((monitor.SelectedItem as Item)?.Value is MonitorInfo m) s.MonitorDeviceName = m.DeviceName;
        s.Fps = (int)fps.SelectedItem!;
        s.CaptureSystemAudio = systemAudio.Checked;
        s.AudioSource = (AudioSource)((Item)audioSource.SelectedItem!).Value!;
        s.CaptureMicrophone = mic.Checked;
        s.MicrophoneDeviceId = (micDevice.SelectedItem as Item)?.Value as string;
        s.Hotkey = hotkeyBox.Text;
        s.OutputFolder = folder.Text.Trim();
        s.Encoder = (EncoderPreference)((Item)encoder.SelectedItem!).Value!;
        s.BitrateMbps = (int)bitrate.Value;
        s.AudioOffsetMs = (int)offset.Value;
        s.StartWithWindows = startWithWindows.Checked;
        s.StartReplayOnLaunch = startOnLaunch.Checked;
        s.CheckForUpdates = checkUpdates.Checked;
        s.Language = (Language)((Item)language.SelectedItem!).Value!;
        DialogResult = DialogResult.OK;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            captionTimer.Dispose();
            StopPreview();
            preview.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(12), UseVisualStyleBackColor = true };
        page.Controls.Add(content);
        return page;
    }

    static TableLayoutPanel Grid() =>
        new() { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(12, 12) };

    static void Row(TableLayoutPanel grid, string label, params Control[] controls)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(controls.Length == 1 ? controls[0] : Flow(controls));
    }

    static Image? IconOf(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        using var icon = Icon.ExtractAssociatedIcon(path);
        return icon?.ToBitmap();
    }

    static NumericUpDown Number(int min, int max, int increment) =>
        new() { Minimum = min, Maximum = max, Increment = increment, Width = 90 };

    static ComboBox Dropdown(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true };

    static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left };

    static FlowLayoutPanel Flow(Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }

    static FlowLayoutPanel Stack(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }
}
