namespace Cliperino.Core;

public static class Downloader
{
    const int BufferSize = 1 << 16;

    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cliperino");
        return http;
    }

    public static async Task DownloadAsync(
        string url, string path, string label, IProgress<(string Text, int? Percent)> progress, CancellationToken cancellation)
    {
        try
        {
            using var http = CreateClient(Timeout.InfiniteTimeSpan);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var target = File.Create(path);
            var buffer = new byte[BufferSize];
            long done = 0;
            int read, lastMark = -1;
            while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                done += read;
                int mark = total > 0 ? (int)(done * 100 / total.Value) : (int)(done >> 20);
                if (mark == lastMark) continue;
                lastMark = mark;
                progress.Report(total > 0
                    ? ($"{label}… {done >> 20} de {total.Value >> 20} MB", mark)
                    : ($"{label}… {mark} MB", null));
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
