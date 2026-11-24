using Tactile.Core;
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
    private const int SetSignal = 4024;
    private const int SignalMusic = 3002;
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
                throw new AudioUnavailableException(new LocalizedMessage("Could not start the Opus encoder.", "Opus kodlayıcı başlatılamadı."));
            }

            Check(Control(encoder, SetBitrate, 160_000));
            Check(Control(encoder, SetVbr, 0));
            // Highest quality search and a music hint; the 200-byte frame size is fixed by the controller.
            Check(Control(encoder, SetComplexity, 10));
            Check(Control(encoder, SetSignal, SignalMusic));
            _encoder = encoder;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            encoder.Dispose();
            throw new AudioUnavailableException(new LocalizedMessage("Could not load opus.dll for the controller speaker.", "Kontrolcü hoparlörü için opus.dll yüklenemedi."), e);
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
            throw new ArgumentException(Text.Pick("Opus requires 480 stereo samples.", "Opus için 480 stereo örnek gerekli."), nameof(stereo480));
        }

        if (frame200.Length != FrameBytes)
        {
            throw new ArgumentException(Text.Pick("The Opus packet must be 200 bytes.", "Opus paketi 200 bayt olmalı."), nameof(frame200));
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
            throw new AudioUnavailableException(new LocalizedMessage($"Opus failed for the controller speaker ({result}).", $"Kontrolcü hoparlörü için Opus işlemi başarısız oldu ({result})."));
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

/// <summary>Decodes the controller's mono 48 kHz 10 ms Opus frames.</summary>
public sealed partial class OpusDecoder : IMicDecoder
{
    public const int SampleRate = 48000, Channels = 1, FrameSamples = 480;

    private readonly DecoderHandle _decoder;

    public OpusDecoder()
    {
        var decoder = new DecoderHandle();
        try
        {
            decoder.Initialize(Create(SampleRate, Channels, out int error));
            if (error < 0 || decoder.IsInvalid)
            {
                throw new AudioUnavailableException(new LocalizedMessage($"Could not start the Opus decoder ({error}).", $"Opus çözücü başlatılamadı ({error})."));
            }

            _decoder = decoder;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            decoder.Dispose();
            throw new AudioUnavailableException(new LocalizedMessage("Could not load opus.dll for the controller microphone.", "Kontrolcü mikrofonu için opus.dll yüklenemedi."), e);
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    /// <returns>Decoded samples per channel, or a negative Opus error code.</returns>
    public unsafe int Decode(ReadOnlySpan<byte> packet, Span<short> mono480)
    {
        if (mono480.Length < FrameSamples || packet.IsEmpty)
        {
            return -1;
        }

        fixed (byte* data = packet)
        fixed (short* pcm = mono480)
        {
            return Decode(_decoder, data, packet.Length, pcm, FrameSamples, 0);
        }
    }

    /// <summary>Packet loss concealment for one lost frame.</summary>
    public unsafe int DecodePlc(Span<short> mono480)
    {
        if (mono480.Length < FrameSamples)
        {
            return -1;
        }

        fixed (short* pcm = mono480)
        {
            return Decode(_decoder, null, 0, pcm, FrameSamples, 0);
        }
    }

    public void Dispose() => _decoder.Dispose();

    private sealed class DecoderHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        public void Initialize(nint value) => SetHandle(value);

        protected override bool ReleaseHandle()
        {
            Destroy(handle);
            return true;
        }
    }

    [LibraryImport("opus", EntryPoint = "opus_decoder_create")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static partial nint Create(int rate, int channels, out int error);

    [LibraryImport("opus", EntryPoint = "opus_decode")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static unsafe partial int Decode(DecoderHandle decoder, byte* data, int length, short* pcm, int frameSize, int fec);

    [LibraryImport("opus", EntryPoint = "opus_decoder_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static partial void Destroy(nint decoder);
}
