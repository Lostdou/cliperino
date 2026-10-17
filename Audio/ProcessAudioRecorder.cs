using System.Runtime.InteropServices;
using Cliperino.Core;

namespace Cliperino.Audio;

// captura solo el audio de un proceso y sus hijos; requiere windows 11
public sealed class ProcessAudioRecorder : IAudioRecorder
{
    const int SampleRate = 48000;
    const int Channels = 2;
    const int BlockAlign = Channels * 2;
    static readonly long BufferDuration = TimeSpan.FromMilliseconds(100).Ticks;
    static readonly Guid AudioCaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    readonly ProcessLoopback.IAudioClient client;
    readonly ProcessLoopback.IAudioCaptureClient capture;
    readonly AutoResetEvent packetReady = new(false);
    readonly Thread thread;
    volatile bool stopping;

    public AudioRingBuffer Ring { get; }

    ProcessAudioRecorder(ProcessLoopback.IAudioClient client, double seconds)
    {
        this.client = client;
        Ring = new AudioRingBuffer(SampleRate, Channels, seconds);
        var format = new ProcessLoopback.WaveFormatEx
        {
            FormatTag = 1,
            Channels = Channels,
            SamplesPerSec = SampleRate,
            AvgBytesPerSec = SampleRate * BlockAlign,
            BlockAlign = BlockAlign,
            BitsPerSample = 16,
        };
        var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ProcessLoopback.WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, formatPtr, false);
            // el proceso no tiene formato de mezcla propio: se pide pcm16 y windows convierte
            client.Initialize(ProcessLoopback.SharedMode, ProcessLoopback.CaptureFlags, BufferDuration, 0, formatPtr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }
        client.GetService(AudioCaptureClientId, out var service);
        capture = (ProcessLoopback.IAudioCaptureClient)service;
        client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
        thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Cliperino process audio" };
        thread.Start();
    }

    public static async Task<ProcessAudioRecorder> StartAsync(int processId, double seconds)
    {
        var client = await ProcessLoopback.ActivateAsync(processId);
        Log.Write($"Audio (juego): proceso {processId}, {SampleRate} Hz, {Channels} canales");
        return new ProcessAudioRecorder(client, seconds);
    }

    void CaptureLoop()
    {
        try
        {
            var buffer = new byte[SampleRate * BlockAlign];
            client.Start();
            while (!stopping)
            {
                packetReady.WaitOne(100);
                // se junta todo lo pendiente en una sola escritura para que el ring no lo tome como atrasado
                int total = 0;
                capture.GetNextPacketSize(out var pending);
                while (pending > 0)
                {
                    capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                    int bytes = (int)frames * BlockAlign;
                    if (total + bytes > buffer.Length) Array.Resize(ref buffer, (total + bytes) * 2);
                    if ((flags & ProcessLoopback.BufferFlagsSilent) != 0) Array.Clear(buffer, total, bytes);
                    else Marshal.Copy(data, buffer, total, bytes);
                    capture.ReleaseBuffer(frames);
                    total += bytes;
                    capture.GetNextPacketSize(out pending);
                }
                if (total > 0) Ring.Write(buffer.AsSpan(0, total), DateTime.UtcNow);
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Log.Write($"Se detuvo el audio del juego: {ex.Message}");
        }
    }

    public void Dispose()
    {
        stopping = true;
        packetReady.Set();
        thread.Join(1000);
        Marshal.ReleaseComObject(capture);
        Marshal.ReleaseComObject(client);
        packetReady.Dispose();
    }
}
