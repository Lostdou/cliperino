namespace Cliperino.Capture;

static class MpegTs
{
    public const int PacketSize = 188;
    const long PtsWrap = 1L << 33;
    const double ClockRate = 90_000;
    const int TailBytes = 4 * 1024 * 1024;

    public static long? FirstPts(string path)
    {
        try
        {
            using var file = Open(path);
            var buffer = new byte[256 * PacketSize];
            int read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Scan(buffer.AsSpan(0, read), first: true);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static long? LastPts(string path, long length)
    {
        try
        {
            using var file = Open(path);
            long start = Math.Max(0, length - TailBytes) / PacketSize * PacketSize;
            file.Position = start;
            var buffer = new byte[length - start];
            int read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Scan(buffer.AsSpan(0, read), first: false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    // resta modulo 2^33 porque los pts de mpeg-ts dan la vuelta cada 20hs
    public static TimeSpan Elapsed(long from, long to)
    {
        long diff = ((to - from) % PtsWrap + PtsWrap) % PtsWrap;
        if (diff >= PtsWrap / 2) diff -= PtsWrap;
        return TimeSpan.FromSeconds(diff / ClockRate);
    }

    static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static long? Scan(ReadOnlySpan<byte> data, bool first)
    {
        long? found = null;
        for (int offset = 0; offset + PacketSize <= data.Length; offset += PacketSize)
        {
            if (!TryReadPts(data.Slice(offset, PacketSize), out var pts)) continue;
            if (first) return pts;
            found = pts;
        }
        return found;
    }

    static bool TryReadPts(ReadOnlySpan<byte> packet, out long pts)
    {
        pts = 0;
        bool payloadStart = packet[0] == 0x47 && (packet[1] & 0x40) != 0;
        int adaptation = (packet[3] >> 4) & 0x3;
        if (!payloadStart || adaptation is 0 or 2) return false;
        int payload = adaptation == 3 ? 5 + packet[4] : 4;
        if (payload + 14 > PacketSize) return false;
        var pes = packet[payload..];
        bool videoPes = pes[0] == 0 && pes[1] == 0 && pes[2] == 1 && (pes[3] & 0xF0) == 0xE0;
        if (!videoPes || (pes[7] & 0x80) == 0) return false;
        pts = ((long)(pes[9] >> 1) & 0x07) << 30 | (long)pes[10] << 22 | (long)(pes[11] >> 1) << 15 |
              (long)pes[12] << 7 | (long)(pes[13] >> 1);
        return true;
    }
}
