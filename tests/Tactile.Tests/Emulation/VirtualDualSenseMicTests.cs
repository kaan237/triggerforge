using Tactile.Emulation;

namespace Tactile.Tests.Emulation;

public class VirtualDualSenseMicTests
{
    [Fact]
    public void Mic_active_tracks_interface2_alt_reset_detach_and_applies_uac1_mute()
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense.WithAudio)server.CreateDualSense(null, audio: true);
        var changes = new List<bool>();
        pad.MicActiveChanged += changes.Add;
        ControlResult Set(ushort index, ushort alt) => pad.Control(new SetupPacket(1, 0x0B, alt, index, 0), [], 0);

        Assert.False(pad.MicActive);
        Assert.Equal(0, Set(1, 1).Status);
        Assert.Empty(changes);
        Assert.Equal(0, Set(2, 1).Status);
        Assert.True(pad.MicActive);
        Assert.Equal(0, Set(2, 1).Status);
        Assert.Equal([true], changes);
        Assert.Equal(0, Set(2, 0).Status);
        Assert.False(pad.MicActive);
        Assert.Equal([true, false], changes);

        Set(2, 1);
        Assert.Equal(0, pad.Control(new SetupPacket(0, 9, 1, 0, 0), [], 0).Status);
        Assert.False(pad.MicActive);
        Set(2, 1);
        pad.Detach();
        Assert.False(pad.MicActive);
        Assert.Equal([true, false, true, false, true, false], changes);

        Set(2, 1);
        pad.SetMicReader(span => span[..100].Fill(0x11));
        byte[] data = new byte[192];
        Assert.Equal(192, pad.AudioIn(data));
        Assert.Equal(0x11, data[99]);
        Assert.Equal(0, data[100]);
        Assert.Equal(96, pad.AudioIn(new byte[96]));
        Assert.Equal(192, pad.AudioIn(new byte[400]));

        Assert.Equal(0, pad.Control(new SetupPacket(0x21, 0x01, 0x0100, 0x0500, 1), [1], 0).Status);
        Assert.True(pad.MicMuted);
        Array.Fill(data, (byte)0xAA);
        pad.AudioIn(data);
        Assert.All(data, b => Assert.Equal(0, b));

        pad.Control(new SetupPacket(0x21, 0x01, 0x0100, 0x0500, 1), [0], 0);
        pad.SetMicReader(null);
        Array.Fill(data, (byte)0xAA);
        pad.AudioIn(data);
        Assert.All(data, b => Assert.Equal(0, b));
        pad.SetMicReader(_ => throw new InvalidOperationException());
        Assert.Equal(192, pad.AudioIn(data));

        Set(2, 0);
        Assert.Equal(0, pad.AudioIn(data));
    }

    private static (UsbIpServer Server, VirtualDualSense.WithAudio Pad) Recording()
    {
        var server = new UsbIpServer(0);
        var pad = (VirtualDualSense.WithAudio)server.CreateDualSense(null, audio: true);
        pad.Control(new SetupPacket(1, 0x0B, 1, 2, 0), [], 0);
        return (server, pad);
    }

    [Fact]
    public void Muted_endpoint_still_drains_the_reader_and_sends_silence()
    {
        (UsbIpServer server, VirtualDualSense.WithAudio pad) = Recording();
        using (server)
        {
            int reads = 0;
            pad.SetMicReader(span =>
            {
                reads++;
                span.Fill(0x22);
            });
            pad.Control(new SetupPacket(0x21, 0x01, 0x0100, 0x0500, 1), [1], 0);
            byte[] data = new byte[192];
            for (int i = 0; i < 5; i++)
            {
                Array.Fill(data, (byte)0xAA);
                Assert.Equal(192, pad.AudioIn(data));
                Assert.All(data, b => Assert.Equal(0, b));
            }

            Assert.Equal(5, reads);
            pad.Control(new SetupPacket(0x21, 0x01, 0x0100, 0x0500, 1), [0], 0);
            pad.AudioIn(data);
            Assert.Equal(0x22, data[0]);
        }
    }

    [Fact]
    public void Mic_volume_attenuates_like_the_speaker_volume_and_the_top_leaves_samples_alone()
    {
        (UsbIpServer server, VirtualDualSense.WithAudio pad) = Recording();
        using (server)
        {
            pad.SetMicReader(span =>
            {
                for (int i = 0; i < span.Length; i += 2)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span[i..], 10000);
                }
            });
            byte[] data = new byte[192];
            Assert.Equal(0x3000, pad.MicVolume);
            pad.AudioIn(data);
            Assert.Equal(10000, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data));

            // 6 dB below the top is about half.
            pad.Control(new SetupPacket(0x21, 0x01, 0x0200, 0x0500, 2), [0x00, 0x2A], 0);
            Assert.Equal(0x2A00, pad.MicVolume);
            pad.AudioIn(data);
            short half = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data);
            Assert.InRange(half, 4900, 5100);

            pad.Control(new SetupPacket(0x21, 0x01, 0x0200, 0x0500, 2), [0x00, 0x00], 0);
            pad.AudioIn(data);
            Assert.InRange(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data), 0, 100);
        }
    }

    [Fact]
    public void Mic_volume_is_signed_with_0x8000_as_silence_and_never_amplifies()
    {
        (UsbIpServer server, VirtualDualSense.WithAudio pad) = Recording();
        using (server)
        {
            pad.SetMicReader(span =>
            {
                for (int i = 0; i < span.Length; i += 2)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span[i..], 10000);
                }
            });
            byte[] data = new byte[192];
            short First() => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data);
            void Set(ushort volume) => pad.Control(new SetupPacket(0x21, 0x01, 0x0200, 0x0500, 2), [(byte)volume, (byte)(volume >> 8)], 0);

            Set(0x8000);
            Assert.Equal(0x8000, pad.MicVolume);
            Array.Fill(data, (byte)0xAA);
            pad.AudioIn(data);
            Assert.All(data, b => Assert.Equal(0, b));

            // A negative value (as a signed 1/256 dB number) is quieter than the bottom of the usual range, not unity.
            Set(0xF000);
            pad.AudioIn(data);
            Assert.InRange(First(), 0, 100);

            // Above the top never amplifies.
            foreach (ushort loud in new ushort[] { 0x3000, 0x3800, 0x7FFF })
            {
                Set(loud);
                pad.AudioIn(data);
                Assert.Equal(10000, First());
            }

            Set(0x2A00);
            pad.AudioIn(data);
            Assert.InRange(First(), 4900, 5100);
        }
    }

    [Fact]
    public void A_throwing_active_listener_does_not_break_the_usb_request()
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense.WithAudio)server.CreateDualSense(null, audio: true);
        pad.MicActiveChanged += _ => throw new InvalidOperationException();
        Assert.Equal(0, pad.Control(new SetupPacket(1, 0x0B, 1, 2, 0), [], 0).Status);
        Assert.True(pad.MicActive);
        pad.Detach();
        Assert.False(pad.MicActive);
    }
}
