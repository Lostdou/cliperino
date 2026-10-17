using Cliperino.Ffmpeg;

namespace Cliperino.UI;

sealed record PreviewSource(string? Ffmpeg, string? SegmentPath, string Caption);

sealed class PreviewForm : Form
{
    static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    readonly Func<PreviewSource> source;
    readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly Label caption = new()
    {
        Dock = DockStyle.Bottom,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 8, 0),
    };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    string? shownSegment;
    bool loading;

    public PreviewForm(Func<PreviewSource> source)
    {
        this.source = source;
        Text = "Cliperino – Vista previa";
        Font = SystemFonts.MessageBoxFont ?? Font;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 388);
        MinimumSize = new Size(320, 220);
        Controls.Add(picture);
        Controls.Add(caption);

        timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) =>
        {
            timer.Start();
            await RefreshAsync();
        };
        FormClosed += (_, _) => timer.Stop();
    }

    async Task RefreshAsync()
    {
        if (loading) return;
        var current = source();
        caption.Text = current.Caption;
        if (current.Ffmpeg == null || current.SegmentPath == null)
        {
            SetImage(null);
            shownSegment = null;
            return;
        }
        if (current.SegmentPath == shownSegment) return;

        loading = true;
        try
        {
            var frame = await Task.Run(() => ReadFirstFrameAsync(current.Ffmpeg, current.SegmentPath));
            if (frame == null || IsDisposed) return;
            SetImage(frame);
            shownSegment = current.SegmentPath;
        }
        finally
        {
            loading = false;
        }
    }

    static async Task<Image?> ReadFirstFrameAsync(string ffmpeg, string segment)
    {
        var args = $"-hide_banner -loglevel error -nostdin -i \"{segment}\" -frames:v 1 -vf scale=960:-2 -f image2pipe -c:v bmp -";
        var bytes = await FfmpegProcess.CaptureOutputAsync(ffmpeg, args, FrameTimeout);
        if (bytes is not { Length: > 0 }) return null;
        using var stream = new MemoryStream(bytes);
        using var decoded = Image.FromStream(stream);
        return new Bitmap(decoded);
    }

    void SetImage(Image? image)
    {
        var previous = picture.Image;
        picture.Image = image;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
