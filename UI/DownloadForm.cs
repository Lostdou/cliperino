using static Cliperino.Core.Lang;
namespace Cliperino.UI;

sealed class DownloadForm : Form
{
    readonly Label status = new() { Text = T("Conectando…", "Connecting…"), AutoSize = true };
    readonly ProgressBar bar = new() { Width = 380, Style = ProgressBarStyle.Marquee };
    readonly CancellationTokenSource cancellation = new();

    public CancellationToken Cancellation => cancellation.Token;

    public DownloadForm(string title)
    {
        Text = title;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var cancel = new Button { Text = T("Cancelar", "Cancel"), AutoSize = true, Anchor = AnchorStyles.Right };
        cancel.Click += (_, _) => Close();
        FormClosing += (_, _) => cancellation.Cancel();

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.AddRange(new Control[] { status, bar, cancel });
        Controls.Add(layout);
        CancelButton = cancel;
    }

    public void Report(string text, int? percent)
    {
        // progress<t> postea asincronico y puede llegar con la ventana ya cerrada
        if (IsDisposed) return;
        status.Text = text;
        if (percent is not int value) return;
        bar.Style = ProgressBarStyle.Continuous;
        bar.Value = Math.Clamp(value, 0, 100);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) cancellation.Dispose();
        base.Dispose(disposing);
    }
}
