using Cliperino.Capture;
using Cliperino.Core;

namespace Cliperino.UI;

sealed class SettingsForm : Form
{
    sealed record Item(string Text, object? Value)
    {
        public override string ToString() => Text;
    }

    readonly AppSettings s;
    readonly NumericUpDown seconds = Number(5, 1200, 5);
    readonly Label secondsHint = Hint("");
    readonly ComboBox fps = Dropdown(90);
    readonly NumericUpDown bitrate = Number(2, 150, 1);
    readonly ComboBox monitor = Dropdown(380);
    readonly ComboBox encoder = Dropdown(380);
    readonly Label detected = Hint("");
    readonly TextBox hotkeyBox = new() { ReadOnly = true, Width = 160, BackColor = SystemColors.Window };
    readonly TextBox folder = new() { Width = 340 };
    readonly CheckBox systemAudio = Check("Grabar audio del sistema");
    readonly CheckBox mic = Check("Micrófono:");
    readonly ComboBox micDevice = Dropdown(300);
    readonly NumericUpDown offset = Number(-2000, 2000, 10);
    readonly CheckBox startWithWindows = Check("Iniciar con Windows");
    readonly CheckBox startOnLaunch = Check("Activar la repetición al abrir Cliperino");

    public AppSettings Result => s;
    public bool ForceRedetect { get; private set; }

    public SettingsForm(AppSettings settings, GpuSnapshot gpus, string? activePipeline, IReadOnlyList<(string Id, string Name)> microphones)
    {
        s = settings;
        Text = "Cliperino – Ajustes";
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        seconds.Value = s.ReplaySeconds;
        seconds.ValueChanged += (_, _) => secondsHint.Text = "= " + TrayApp.FormatDuration((int)seconds.Value);
        secondsHint.Text = "= " + TrayApp.FormatDuration(s.ReplaySeconds);

        fps.Items.AddRange(new object[] { 30, 60 });
        fps.SelectedItem = s.Fps;
        bitrate.Value = s.BitrateMbps;

        foreach (var m in gpus.Monitors) monitor.Items.Add(new Item(m.Label, m));
        var currentMonitor = gpus.FindMonitor(s.MonitorDeviceName);
        monitor.SelectedItem = monitor.Items.Cast<Item>().FirstOrDefault(i => ReferenceEquals(i.Value, currentMonitor));

        var vendors = gpus.Adapters.Select(a => a.Vendor).ToHashSet();
        encoder.Items.Add(new Item("Automático (recomendado)", EncoderPreference.Auto));
        if (vendors.Contains(GpuVendor.Nvidia)) encoder.Items.Add(new Item("NVIDIA (NVENC)", EncoderPreference.Nvenc));
        if (vendors.Contains(GpuVendor.Amd)) encoder.Items.Add(new Item("AMD (AMF)", EncoderPreference.Amf));
        if (vendors.Contains(GpuVendor.Intel)) encoder.Items.Add(new Item("Intel (Quick Sync)", EncoderPreference.Qsv));
        encoder.Items.Add(new Item("CPU (x264, usa más procesador)", EncoderPreference.X264));
        encoder.SelectedItem = encoder.Items.Cast<Item>().FirstOrDefault(i => Equals(i.Value, s.Encoder)) ?? encoder.Items[0];

        detected.Text = activePipeline ?? "Se detecta al activar la repetición";
        var redetect = new Button { Text = "Volver a detectar", AutoSize = true };
        redetect.Click += (_, _) =>
        {
            ForceRedetect = true;
            detected.Text = "Se volverá a detectar al guardar";
        };

        hotkeyBox.Text = s.Hotkey;
        hotkeyBox.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            if (HotkeyWindow.Format(e.KeyData) is { } text) hotkeyBox.Text = text;
        };

        folder.Text = s.OutputFolder;
        var browse = new Button { Text = "…", Width = 32 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                SelectedPath = folder.Text,
                UseDescriptionForTitle = true,
                Description = "Carpeta donde se guardan los clips",
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        };

        systemAudio.Checked = s.CaptureSystemAudio;
        mic.Checked = s.CaptureMicrophone;
        micDevice.Items.Add(new Item("Predeterminado de Windows", null));
        foreach (var (id, name) in microphones) micDevice.Items.Add(new Item(name, id));
        micDevice.SelectedItem = micDevice.Items.Cast<Item>().FirstOrDefault(i => Equals(i.Value, s.MicrophoneDeviceId)) ?? micDevice.Items[0];
        micDevice.Enabled = mic.Checked;
        mic.CheckedChanged += (_, _) => micDevice.Enabled = mic.Checked;

        offset.Value = s.AudioOffsetMs;
        startWithWindows.Checked = s.StartWithWindows;
        startOnLaunch.Checked = s.StartReplayOnLaunch;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        void Row(string label, params Control[] controls)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            grid.Controls.Add(controls.Length == 1 ? controls[0] : Flow(controls));
        }
        Row("Guardar los últimos (seg):", seconds, secondsHint);
        Row("FPS:", fps);
        Row("Bitrate (Mbps):", bitrate);
        Row("Monitor:", monitor);
        Row("Codificador:", encoder);
        Row("En uso:", detected, redetect);
        Row("Atajo para guardar:", hotkeyBox, Hint("Hacé clic y presioná la combinación"));
        Row("Carpeta de clips:", folder, browse);
        Row("Audio:", systemAudio);
        Row("", mic, micDevice);
        Row("Desfase de audio (ms):", offset, Hint("Subilo si el sonido llega antes que la imagen"));
        Row("Inicio:", startWithWindows);
        Row("", startOnLaunch);

        var save = new Button { Text = "Guardar", AutoSize = true };
        save.Click += OnSave;
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, save });
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);

        Controls.Add(grid);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => Activate();
    }

    void OnSave(object? sender, EventArgs e)
    {
        if (!HotkeyWindow.TryParse(hotkeyBox.Text, out _, out _))
        {
            MessageBox.Show(this, "El atajo no es válido.", "Cliperino", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(folder.Text))
        {
            MessageBox.Show(this, "Elegí una carpeta para los clips.", "Cliperino", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        s.ReplaySeconds = (int)seconds.Value;
        s.Fps = (int)fps.SelectedItem!;
        s.BitrateMbps = (int)bitrate.Value;
        if ((monitor.SelectedItem as Item)?.Value is MonitorInfo m) s.MonitorDeviceName = m.DeviceName;
        s.Encoder = (EncoderPreference)((Item)encoder.SelectedItem!).Value!;
        s.Hotkey = hotkeyBox.Text;
        s.OutputFolder = folder.Text.Trim();
        s.CaptureSystemAudio = systemAudio.Checked;
        s.CaptureMicrophone = mic.Checked;
        s.MicrophoneDeviceId = (micDevice.SelectedItem as Item)?.Value as string;
        s.AudioOffsetMs = (int)offset.Value;
        s.StartWithWindows = startWithWindows.Checked;
        s.StartReplayOnLaunch = startOnLaunch.Checked;
        DialogResult = DialogResult.OK;
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
}
