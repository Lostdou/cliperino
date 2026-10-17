namespace Cliperino.Audio;

public interface IAudioRecorder : IDisposable
{
    AudioRingBuffer? Ring { get; }
}
