using System.Buffers.Binary;
using Tactile.Emulation;

namespace Tactile.Tests.Emulation;

public class VirtualDualSenseAudioTests
{
    [Fact]
    public void Full_speed_config_has_four_interfaces_and_four_endpoints()
    {
        using var server = new UsbIpServer(0);
        var audio = (VirtualDualSense)server.CreateDualSense(null, audio: true);
        IVirtualPad hidOnly = server.CreateDualSense(null, audio: false);
        Assert.False(hidOnly is IVirtualAudio);
        byte[] config = audio.Control(new SetupPacket(0x80, 6, 0x0200, 0, 227), [], 0).Data;

        Assert.Equal(2, audio.Speed);
        Assert.Equal(4, audio.IntervalMs);
        Assert.Equal(227, config.Length);
        Assert.Equal([0x09, 0x02, 0xE3, 0, 4, 1, 0, 0xC0, 0xFA], config[..9]);
        Assert.Equal([(1, 1, 0), (1, 2, 0), (1, 2, 0), (3, 0, 0)],
            UsbIpCodec.Interfaces(config).Select(i => ((int)i.Class, (int)i.SubClass, (int)i.Protocol)));
        var endpoints = new List<byte[]>();
        int offset = 0;
        while (offset < config.Length)
        {
            int length = config[offset];
            Assert.InRange(length, 2, config.Length - offset);
            if (config[offset + 1] == 5)
            {
                endpoints.Add(config[offset..(offset + length)]);
            }

            offset += length;
        }

        Assert.Collection(endpoints,
            e => Assert.Equal([9, 5, 0x01, 0x09, 0x88, 0x01, 1, 0, 0], e),
            e => Assert.Equal([9, 5, 0x82, 0x05, 0xC0, 0, 1, 0, 0], e),
            e => Assert.Equal([7, 5, 0x84, 3, 64, 0, 4], e),
            e => Assert.Equal([7, 5, 0x03, 3, 64, 0, 4], e));
        Assert.Equal([9, 4, 3, 0, 2, 3, 0, 0, 0], config[^32..^23]);
    }

    [Fact]
    public void Hid_descriptors_and_class_requests_work_on_interface_three()
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense)server.CreateDualSense(null, audio: true);
        var hidOnly = (VirtualDualSense)server.Create(VirtualKind.DualSense, null);
        var report = new SetupPacket(0x81, 6, 0x2200, 0, 273);
        byte[] expected = hidOnly.Control(report, [], 0).Data;
        Assert.Equal(273, expected.Length);
        Assert.Equal(expected, pad.Control(report with { Index = 3 }, [], 0).Data);
        Assert.Equal(expected, pad.Control(report, [], 0).Data);
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(report with { Index = 2 }, [], 0).Status);
        Assert.Equal([9, 0x21, 0x11, 1, 0, 1, 0x22, 0x11, 1],
            pad.Control(report with { Index = 3, Value = 0x2100, Length = 9 }, [], 0).Data);
        Assert.Equal([0], pad.Control(new SetupPacket(0xA1, 2, 0, 3, 1), [], 0).Data);
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0x21, 0x0A, 0, 1, 0), [], 0).Status);
    }

    [Fact]
    public void Alternate_settings_control_activity_and_configuration_resets_them()
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense.WithAudio)server.CreateDualSense(null, audio: true);
        ControlResult Set(ushort index, ushort alt) => pad.Control(new SetupPacket(1, 0x0B, alt, index, 0), [], 0);
        byte[] Get(ushort index) => pad.Control(new SetupPacket(0x81, 0x0A, 0, index, 1), [], 0).Data;

        Assert.False(pad.SpeakerActive);
        Assert.Equal(0, Set(1, 1).Status);
        Assert.True(pad.SpeakerActive);
        Assert.Equal(0, Set(2, 1).Status);
        Assert.Equal([1], Get(1));
        Assert.Equal([1], Get(2));
        Assert.Equal(UsbIpCodec.EPipe, Set(1, 2).Status);
        Assert.Equal(UsbIpCodec.EPipe, Set(0, 1).Status);
        Assert.Equal(UsbIpCodec.EPipe, Set(3, 1).Status);
        Assert.Equal(UsbIpCodec.EPipe, Set(4, 0).Status);
        Assert.Equal([0], Get(0));
        Assert.Equal([0], Get(3));
        Assert.Equal(0, Set(1, 0).Status);
        Assert.False(pad.SpeakerActive);
        Assert.Equal([0], Get(1));
        Assert.Equal(0, Set(1, 1).Status);

        Assert.Equal(0, pad.Control(new SetupPacket(0, 9, 1, 0, 0), [], 0).Status);
        Assert.False(pad.SpeakerActive);
        Assert.Equal([0], Get(1));
        Assert.Equal([0], Get(2));

        Assert.Equal(0, Set(1, 1).Status);
        pad.Detach();
        Assert.False(pad.SpeakerActive);
    }

    [Theory]
    [InlineData(0x0200, 0x0000, 0x9C00, 0x0000, 0x0100)]
    [InlineData(0x0500, 0x3000, 0x0000, 0x3000, 0x007A)]
    public void Feature_units_return_defaults_limits_and_written_values(ushort index, ushort current, ushort min, ushort max, ushort res)
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense)server.CreateDualSense(null, audio: true);
        ControlResult Get(byte request, ushort value, ushort length) =>
            pad.Control(new SetupPacket(0xA1, request, value, index, length), [], 0);
        ControlResult Set(ushort value, byte[] data) =>
            pad.Control(new SetupPacket(0x21, 1, value, index, (ushort)data.Length), data, 0);

        Assert.Equal([0], Get(0x81, 0x0100, 1).Data);
        Assert.Equal(0, Set(0x0100, [1]).Status);
        Assert.Equal([1], Get(0x81, 0x0100, 1).Data);
        ushort[] expected = [current, min, max, res];
        for (byte i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], BinaryPrimitives.ReadUInt16LittleEndian(Get((byte)(0x81 + i), 0x0200, 2).Data));
            if (i != 0)
            {
                Assert.Equal(UsbIpCodec.EPipe, Get((byte)(0x81 + i), 0x0100, 1).Status);
            }
        }

        Assert.Equal(0, Set(0x0200, [0x34, 0x92]).Status);
        Assert.Equal([0x34, 0x92], Get(0x81, 0x0200, 2).Data);
        Assert.Equal(UsbIpCodec.EPipe, Set(0x0200, [1]).Status);
        Assert.Equal(UsbIpCodec.EPipe, Get(0x81, 0x0300, 2).Status);
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0xA1, 0x81, 0x0100, 0x0300, 1), [], 0).Status);
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0x22, 1, 0x0100, 1, 3), [0x80, 0xBB, 0], 0).Status);
        Assert.Equal(UsbIpCodec.EPipe, pad.Control(new SetupPacket(0xA2, 0x81, 0x0100, 0x82, 3), [], 0).Status);
    }

    [Fact]
    public void Mic_fills_only_the_requested_silence_and_speaker_drops_partial_frames()
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense.WithAudio)server.CreateDualSense(null, audio: true);
        byte[] buffer = Enumerable.Repeat((byte)42, 200).ToArray();
        Assert.Equal(0, pad.AudioIn(buffer));
        Assert.Equal(0, pad.Control(new SetupPacket(1, 0x0B, 1, 2, 0), [], 0).Status);
        Assert.Equal(192, pad.AudioIn(buffer));
        Assert.Equal(new byte[192], buffer[..192]);
        Assert.All(buffer[192..], value => Assert.Equal(42, value));
        Assert.Equal(3, pad.AudioIn(buffer.AsSpan(193, 3)));
        Assert.Equal([42, 0, 0, 0, 42], buffer[192..197]);

        var packets = new List<short[]>();
        pad.Audio += packet => packets.Add(packet.ToArray());
        pad.AudioOut([0x34, 0x12, 0xFF, 0xFF, 0, 0x80, 1, 0, 2, 0, 3]);
        Assert.Equal([(short)0x1234, -1, short.MinValue, 1], Assert.Single(packets));
    }

    [Fact]
    public void Windows_mute_and_volume_scale_the_game_audio()
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense.WithAudio)server.CreateDualSense(null, audio: true);
        short first = 0;
        pad.Audio += samples => first = samples[0];
        byte[] packet = [0x00, 0x40, 0, 0, 0, 0, 0, 0]; // one frame, channel 1 = 16384

        pad.AudioOut(packet);
        Assert.Equal(16384, first);

        // Volume -6 dB (0xFA00) on feature unit 2 halves the samples.
        pad.Control(new SetupPacket(0x21, 0x01, 0x0200, 0x0200, 2), [0x00, 0xFA], 0);
        pad.AudioOut(packet);
        Assert.InRange(first, 8100, 8250);

        // Mute on unit 2 silences them.
        pad.Control(new SetupPacket(0x21, 0x01, 0x0100, 0x0200, 1), [1], 0);
        pad.AudioOut(packet);
        Assert.Equal(0, first);
    }
}
