using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Cliperino.Core;

namespace Cliperino.Audio;

// sin dispositivo elegido sigue al predeterminado, ej: al enchufar auriculares
public sealed class AudioRecorder : IDisposable, IMMNotificationClient
{
    readonly DataFlow flow;
    readonly Role role;
    readonly string? deviceId;
    readonly double seconds;
    readonly MMDeviceEnumerator enumerator = new();
    readonly object gate = new();
    WasapiCapture? capture;
    byte[] scratch = [];
    volatile AudioRingBuffer? ring;
    bool disposed;

    public AudioRingBuffer? Ring => ring;

    string Name => flow == DataFlow.Render ? "sistema" : "micrófono";

    public AudioRecorder(DataFlow flow, string? deviceId, double seconds)
    {
        this.flow = flow;
        role = flow == DataFlow.Render ? Role.Multimedia : Role.Console;
        this.deviceId = deviceId;
        this.seconds = seconds;
        StartCapture();
        if (deviceId == null) enumerator.RegisterEndpointNotificationCallback(this);
    }

    public static IReadOnlyList<(string Id, string Name)> ListMicrophones()
    {
        var list = new List<(string, string)>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var device in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                using (device) list.Add((device.ID, device.FriendlyName));
        }
        catch (COMException ex)
        {
            Log.Write($"No se pudieron listar micrófonos: {ex.Message}");
        }
        return list;
    }

    void StartCapture()
    {
        lock (gate)
        {
            if (disposed) return;
            StopCapture();
            try
            {
                var device = deviceId != null ? enumerator.GetDevice(deviceId) : enumerator.GetDefaultAudioEndpoint(flow, role);
                var newCapture = flow == DataFlow.Render ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
                var format = newCapture.WaveFormat;
                var current = ring;
                if (current == null || current.Format.SampleRate != format.SampleRate || current.Format.Channels != format.Channels)
                    ring = new AudioRingBuffer(format.SampleRate, format.Channels, seconds);
                newCapture.DataAvailable += OnData;
                newCapture.RecordingStopped += OnStopped;
                capture = newCapture;
                newCapture.StartRecording();
                Log.Write($"Audio ({Name}): {device.FriendlyName}, {format}");
            }
            catch (Exception ex)
            {
                Log.Write($"No se pudo iniciar el audio ({Name}): {ex.Message}");
                StopCapture();
            }
        }
    }

    void StopCapture()
    {
        var old = capture;
        capture = null;
        if (old == null) return;
        old.DataAvailable -= OnData;
        old.RecordingStopped -= OnStopped;
        try { old.StopRecording(); } catch { }
        old.Dispose();
    }

    void RestartLater() => Task.Delay(1000).ContinueWith(_ => StartCapture());

    void OnData(object? sender, WaveInEventArgs e)
    {
        var r = ring;
        if (sender != capture || r == null || sender is not IWaveIn source) return;
        r.Write(ToPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), source.WaveFormat), DateTime.UtcNow);
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null || disposed || sender != capture) return;
        Log.Write($"Se detuvo el audio ({Name}): {e.Exception.Message}");
        RestartLater();
    }

    // pcm16 ocupa la mitad de ram que el float32 que entrega wasapi
    ReadOnlySpan<byte> ToPcm16(ReadOnlySpan<byte> source, WaveFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample == 0) return ReadOnlySpan<byte>.Empty;
        int samples = source.Length / bytesPerSample;
        if (scratch.Length < samples * 2) scratch = new byte[samples * 2];
        var target = MemoryMarshal.Cast<byte, short>(scratch.AsSpan(0, samples * 2));
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

        switch (format.BitsPerSample)
        {
            case 32 when isFloat:
                var floats = MemoryMarshal.Cast<byte, float>(source[..(samples * 4)]);
                for (int i = 0; i < samples; i++) target[i] = (short)Math.Clamp(floats[i] * 32767f, -32768f, 32767f);
                break;
            case 32:
                var ints = MemoryMarshal.Cast<byte, int>(source[..(samples * 4)]);
                for (int i = 0; i < samples; i++) target[i] = (short)(ints[i] >> 16);
                break;
            case 24:
                for (int i = 0; i < samples; i++) target[i] = (short)(source[i * 3 + 1] | (source[i * 3 + 2] << 8));
                break;
            case 16:
                source[..(samples * 2)].CopyTo(scratch);
                break;
            default:
                return ReadOnlySpan<byte>.Empty;
        }
        return scratch.AsSpan(0, samples * 2);
    }

    public void OnDefaultDeviceChanged(DataFlow changedFlow, Role changedRole, string defaultDeviceId)
    {
        if (changedFlow == flow && changedRole == role) RestartLater();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            StopCapture();
        }
        if (deviceId == null)
        {
            try { enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        }
        enumerator.Dispose();
    }
}
