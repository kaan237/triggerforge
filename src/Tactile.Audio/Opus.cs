using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tactile.Audio;

public sealed partial class OpusEncoder : IDisposable
{
    public const int SampleRate = 48000, Channels = 2, FrameSamples = 480, FrameBytes = 200;

    private const int RestrictedLowDelay = 2051;
    private const int SetBitrate = 4002;
    private const int SetVbr = 4006;
    private const int SetComplexity = 4010;
    private readonly EncoderHandle _encoder;

    public OpusEncoder()
    {
        var encoder = new EncoderHandle();
        try
        {
            encoder.Initialize(Create(SampleRate, Channels, RestrictedLowDelay, out int error));
            Check(error);
            if (encoder.IsInvalid)
            {
                throw new AudioUnavailableException("Opus kodlayıcı başlatılamadı.");
            }

            Check(Control(encoder, SetBitrate, 160_000));
            Check(Control(encoder, SetVbr, 0));
            Check(Control(encoder, SetComplexity, 5));
            _encoder = encoder;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            encoder.Dispose();
            throw new AudioUnavailableException("Kontrolcü hoparlörü için opus.dll yüklenemedi.", e);
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    public unsafe void Encode(ReadOnlySpan<float> stereo480, Span<byte> frame200)
    {
        if (stereo480.Length != FrameSamples * Channels)
        {
            throw new ArgumentException("Opus için 480 stereo örnek gerekli.", nameof(stereo480));
        }

        if (frame200.Length != FrameBytes)
        {
            throw new ArgumentException("Opus paketi 200 bayt olmalı.", nameof(frame200));
        }

        fixed (float* pcm = stereo480)
        fixed (byte* packet = frame200)
        {
            int bytes = EncodeFloat(_encoder, pcm, FrameSamples, packet, FrameBytes);
            Check(bytes);
            if (bytes < FrameBytes)
            {
                Check(PadPacket(packet, bytes, FrameBytes));
            }
        }
    }

    public void Dispose() => _encoder.Dispose();

    private static void Check(int result)
    {
        if (result < 0)
        {
            throw new AudioUnavailableException($"Kontrolcü hoparlörü için Opus işlemi başarısız oldu ({result}).");
        }
    }

    private sealed class EncoderHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        public void Initialize(nint value) => SetHandle(value);

        protected override bool ReleaseHandle()
        {
            Destroy(handle);
            return true;
        }
    }

    [LibraryImport("opus", EntryPoint = "opus_encoder_create")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static partial nint Create(int rate, int channels, int application, out int error);

    [LibraryImport("opus", EntryPoint = "opus_encoder_ctl")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static partial int Control(EncoderHandle encoder, int request, int value);

    [LibraryImport("opus", EntryPoint = "opus_encode_float")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static unsafe partial int EncodeFloat(EncoderHandle encoder, float* pcm, int samples, byte* packet, int capacity);

    [LibraryImport("opus", EntryPoint = "opus_packet_pad")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static unsafe partial int PadPacket(byte* packet, int bytes, int paddedBytes);

    [LibraryImport("opus", EntryPoint = "opus_encoder_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static partial void Destroy(nint encoder);
}
