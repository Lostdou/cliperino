using NAudio.Wave;

namespace Cliperino.Audio;

// anclado al reloj para alinear el audio con los segmentos de video por hora
public sealed class AudioRingBuffer
{
    static readonly TimeSpan MaxDrift = TimeSpan.FromMilliseconds(100);

    readonly object gate = new();
    readonly byte[] buffer;
    readonly long capacityFrames;
    readonly int blockAlign;
    readonly long maxDriftFrames;
    readonly DateTime startUtc = DateTime.UtcNow;
    long writtenFrames;

    public WaveFormat Format { get; }

    public AudioRingBuffer(int sampleRate, int channels, double seconds)
    {
        Format = new WaveFormat(sampleRate, 16, channels);
        blockAlign = Format.BlockAlign;
        capacityFrames = (long)(seconds * sampleRate);
        buffer = new byte[capacityFrames * blockAlign];
        maxDriftFrames = (long)(sampleRate * MaxDrift.TotalSeconds);
    }

    public void Write(ReadOnlySpan<byte> pcm, DateTime nowUtc)
    {
        lock (gate)
        {
            long frames = pcm.Length / blockAlign;
            long drift = FrameAt(nowUtc) - frames - writtenFrames;
            if (drift > maxDriftFrames)
            {
                // wasapi loopback no entrega datos mientras no suena nada
                WriteSilence(drift);
            }
            else if (drift < -maxDriftFrames)
            {
                // el reloj del dispositivo deriva respecto del sistema
                long drop = Math.Min(frames, -drift);
                pcm = pcm[(int)(drop * blockAlign)..];
                frames -= drop;
            }
            WriteFrames(pcm[..(int)(frames * blockAlign)]);
        }
    }

    public byte[] Extract(DateTime fromUtc, DateTime toUtc)
    {
        lock (gate)
        {
            long from = FrameAt(fromUtc);
            long to = Math.Max(from, FrameAt(toUtc));
            var result = new byte[(to - from) * blockAlign];
            long oldest = Math.Max(0, writtenFrames - capacityFrames);
            long end = Math.Min(to, writtenFrames);
            for (long frame = Math.Max(from, oldest); frame < end;)
            {
                long pos = frame % capacityFrames;
                long count = Math.Min(end - frame, capacityFrames - pos);
                Buffer.BlockCopy(buffer, (int)(pos * blockAlign), result, (int)((frame - from) * blockAlign), (int)(count * blockAlign));
                frame += count;
            }
            return result;
        }
    }

    long FrameAt(DateTime utc) => (long)((utc - startUtc).TotalSeconds * Format.SampleRate);

    void WriteSilence(long frames)
    {
        if (frames >= capacityFrames)
        {
            Array.Clear(buffer);
            writtenFrames += frames;
            return;
        }
        long pos = writtenFrames % capacityFrames;
        long first = Math.Min(frames, capacityFrames - pos);
        Array.Clear(buffer, (int)(pos * blockAlign), (int)(first * blockAlign));
        if (frames > first) Array.Clear(buffer, 0, (int)((frames - first) * blockAlign));
        writtenFrames += frames;
    }

    void WriteFrames(ReadOnlySpan<byte> data)
    {
        long frames = data.Length / blockAlign;
        if (frames > capacityFrames)
        {
            data = data[(int)((frames - capacityFrames) * blockAlign)..];
            writtenFrames += frames - capacityFrames;
            frames = capacityFrames;
        }
        long pos = writtenFrames % capacityFrames;
        int firstBytes = (int)(Math.Min(frames, capacityFrames - pos) * blockAlign);
        data[..firstBytes].CopyTo(buffer.AsSpan((int)(pos * blockAlign)));
        data[firstBytes..].CopyTo(buffer);
        writtenFrames += frames;
    }
}
