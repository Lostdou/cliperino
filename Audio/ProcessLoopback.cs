using System.Runtime.InteropServices;

namespace Cliperino.Audio;

static class ProcessLoopback
{
    public const int SharedMode = 0;
    public const uint CaptureFlags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;
    public const uint BufferFlagsSilent = 0x2;

    const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    const string VirtualDevice = @"VAD\Process_Loopback";
    const ushort VT_BLOB = 65;
    const int ActivationTypeProcessLoopback = 1;
    const int IncludeTargetProcessTree = 0;
    static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    public static async Task<IAudioClient> ActivateAsync(int processId)
    {
        var parameters = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)processId,
            LoopbackMode = IncludeTargetProcessTree,
        };
        int parametersSize = Marshal.SizeOf<ActivationParams>();
        var parametersPtr = Marshal.AllocHGlobal(parametersSize);
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
        try
        {
            Marshal.StructureToPtr(parameters, parametersPtr, false);
            Marshal.StructureToPtr(new BlobVariant { Type = VT_BLOB, Size = (uint)parametersSize, Data = parametersPtr }, variantPtr, false);
            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDevice, AudioClientId, variantPtr, handler, out var operation);
            var client = await handler.Result;
            GC.KeepAlive(operation);
            return client;
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(parametersPtr);
        }
    }

    [ComVisible(true)]
    sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        readonly TaskCompletionSource<IAudioClient> source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Result => source.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int result, out object client);
                Marshal.ThrowExceptionForHR(result);
                source.SetResult((IAudioClient)client);
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BlobVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    // activate audio interface async exige un handler agil o falla con e_illegal_method_call
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject;

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}
