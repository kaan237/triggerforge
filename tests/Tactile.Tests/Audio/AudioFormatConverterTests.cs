using System.Buffers.Binary;
using Tactile.Audio;

namespace Tactile.Tests.Audio;

public sealed class AudioFormatConverterTests
{
    [Fact]
    public void MonoIsDuplicatedToBothChannels()
    {
        var converter = new AudioFormatConverter(new WaveFormat(WaveFormat.Float, 1, 48_000, 32));

        Assert.Equal(new float[] { 0.25f, 0.25f, -0.5f, -0.5f },
            converter.Convert(FloatPacket(0.25f, -0.5f), 2).ToArray());
    }

    [Fact]
    public void MultichannelKeepsTheFirstTwoSamplesOfEachFrame()
    {
        var converter = new AudioFormatConverter(new WaveFormat(WaveFormat.Float, 4, 48_000, 32));

        Assert.Equal(new float[] { 0.75f, 0.25f, -0.75f, -0.25f },
            converter.Convert(FloatPacket(0.75f, 0.25f, 1, -1, -0.75f, -0.25f, 1, -1), 2).ToArray());
    }

    [Fact]
    public void UpsamplingInterpolatesAcrossPacketBoundary()
    {
        var converter = new AudioFormatConverter(new WaveFormat(WaveFormat.Float, 2, 24_000, 32));

        Assert.Equal(new float[] { 0, 0 }, converter.Convert(FloatPacket(0, 0), 1).ToArray());
        Assert.Equal(new float[] { 0.5f, -0.5f, 1, -1 },
            converter.Convert(FloatPacket(1, -1), 1).ToArray());
    }

    [Fact]
    public void DownsamplingCarriesFractionalPositionAcrossPackets()
    {
        var converter = new AudioFormatConverter(new WaveFormat(WaveFormat.Float, 1, 72_000, 32));

        Assert.Equal(new float[] { 0, 0 }, converter.Convert(FloatPacket(0, 0.25f), 2).ToArray());
        Assert.Equal(new float[] { 0.375f, 0.375f, 0.75f, 0.75f },
            converter.Convert(FloatPacket(0.5f, 0.75f), 2).ToArray());
    }

    [Fact]
    public void SilentPacketOutputsOnlyZerosEvenWhenResampling()
    {
        var converter = new AudioFormatConverter(new WaveFormat(WaveFormat.Pcm, 1, 24_000, 16));

        Assert.Equal(new float[] { -0.5f, -0.5f }, converter.Convert([0, 0xc0], 1).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 0 }, converter.Convert([], 1, silent: true).ToArray());
    }

    private static byte[] FloatPacket(params float[] samples)
    {
        byte[] packet = new byte[samples.Length * sizeof(float)];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(i * sizeof(float)), samples[i]);
        return packet;
    }
}
