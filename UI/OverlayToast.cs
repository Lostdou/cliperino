using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Cliperino.UI;

sealed class OverlayToast : Form
{
    const int WS_EX_TOPMOST = 0x8;
    const int WS_EX_TRANSPARENT = 0x20;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    const double VisibleOpacity = 0.95;
    const int FadeInMs = 150;
    const int HoldMs = 2500;
    const int FadeOutMs = 300;

    static readonly Color Background = Color.FromArgb(28, 28, 30);
    static readonly Color Accent = Color.FromArgb(229, 72, 77);
    static readonly Color DetailColor = Color.FromArgb(160, 160, 166);

    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    string title = "";
    string? detail;
    float scale = 1;
    Font? titleFont;
    Font? detailFont;
    DateTime shownAt;

    public OverlayToast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        Opacity = 0;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        timer.Tick += (_, _) => Animate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    public void Show(string title, string? detail = null)
    {
        this.title = title;
        this.detail = detail;
        var screen = Screen.FromHandle(GetForegroundWindow());
        scale = DpiOf(screen) / 96f;
        titleFont?.Dispose();
        detailFont?.Dispose();
        titleFont = new Font("Segoe UI Semibold", 15 * scale, GraphicsUnit.Pixel);
        detailFont = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);

        var (titleSize, detailSize) = MeasureText();
        int width = Scaled(56) + Math.Max(titleSize.Width, detailSize.Width) + Scaled(22);
        int height = Math.Max(Scaled(52), titleSize.Height + detailSize.Height + Scaled(26));
        var area = screen.WorkingArea;
        Bounds = new Rectangle(area.Right - width - Scaled(24), area.Top + Scaled(24), width, height);
        Invalidate();

        shownAt = DateTime.UtcNow;
        if (!Visible)
        {
            Opacity = 0;
            base.Show();
        }
        timer.Start();
    }

    int Scaled(float value) => (int)Math.Round(value * scale);

    (Size Title, Size Detail) MeasureText() => (
        TextRenderer.MeasureText(title, titleFont, Size.Empty, TextFormatFlags.NoPadding),
        detail == null ? Size.Empty : TextRenderer.MeasureText(detail, detailFont, Size.Empty, TextFormatFlags.NoPadding));

    void Animate()
    {
        double elapsed = (DateTime.UtcNow - shownAt).TotalMilliseconds;
        if (elapsed < FadeInMs) Opacity = Math.Max(Opacity, VisibleOpacity * elapsed / FadeInMs);
        else if (elapsed < FadeInMs + HoldMs) Opacity = VisibleOpacity;
        else if (elapsed < FadeInMs + HoldMs + FadeOutMs) Opacity = VisibleOpacity * (1 - (elapsed - FadeInMs - HoldMs) / FadeOutMs);
        else
        {
            timer.Stop();
            Opacity = 0;
            Hide();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (titleFont == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var bar = new SolidBrush(Accent)) g.FillRectangle(bar, 0, 0, Scaled(4), Height);

        int dot = Scaled(18);
        var dotBounds = new Rectangle(Scaled(22), (Height - dot) / 2, dot, dot);
        using (var fill = new SolidBrush(Accent)) g.FillEllipse(fill, dotBounds);
        using (var border = new Pen(Color.White, Math.Max(2f, 2.5f * scale))) g.DrawEllipse(border, dotBounds);

        var (titleSize, detailSize) = MeasureText();
        int top = (Height - titleSize.Height - detailSize.Height) / 2;
        int left = Scaled(56);
        TextRenderer.DrawText(g, title, titleFont, new Point(left, top), Color.White, TextFormatFlags.NoPadding);
        if (detail != null)
            TextRenderer.DrawText(g, detail, detailFont, new Point(left, top + titleSize.Height), DetailColor, TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            titleFont?.Dispose();
            detailFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    static int DpiOf(Screen screen)
    {
        var center = new Point(screen.Bounds.Left + screen.Bounds.Width / 2, screen.Bounds.Top + screen.Bounds.Height / 2);
        return GetDpiForMonitor(MonitorFromPoint(center, 2), 0, out var dpi, out _) == 0 ? (int)dpi : 96;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
