using System.Text.Json;
using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

public sealed class SpeakerPairingTests
{
    private readonly HapticReport _builder = new();
    private readonly SpeakerPairing _pairing = new();
    private readonly sbyte[] _haptics = new sbyte[HapticReport.PayloadLength];
    private readonly byte[] _opus = new byte[HapticReport.SpeakerFrameLength];
    private readonly List<byte[]> _writes = [];

    [Fact]
    public void Double_holds_the_first_tick_and_writes_both_frames_in_order()
    {
        Tick(1);
        Assert.Empty(_writes);
        Assert.True(_pairing.HasPending);

        Tick(2);
        byte[] report = Assert.Single(_writes);
        Assert.Equal(0x39, report[0]);
        Assert.Equal(0, report[1]);
        Assert.All(report[12..212], sample => Assert.Equal(1, sample));
        Assert.All(report[212..412], sample => Assert.Equal(2, sample));
        Assert.All(report[414..478], sample => Assert.Equal(1, sample));
        Assert.All(report[478..542], sample => Assert.Equal(2, sample));
        Assert.False(_pairing.HasPending);

        Tick(3);
        Assert.Single(_writes);
        Tick(4);
        Assert.Equal(new byte[] { 0x39, 0x39 }, _writes.Select(r => r[0]));
    }

    [Theory]
    [InlineData(false, SpeakerPacket.Double39, 0x32)]
    [InlineData(true, SpeakerPacket.Single36, 0x36)]
    public void Interruption_flushes_pending_haptics_before_the_current_tick(bool hasSpeaker, SpeakerPacket mode, int currentId)
    {
        Tick(1);
        Tick(2, hasSpeaker, mode);
        Assert.Collection(_writes,
            report => Assert.Equal(new HapticReport().Build(Enumerable.Repeat((sbyte)1, 64).ToArray()), report),
            report =>
            {
                Assert.Equal(currentId, report[0]);
                Assert.Equal(0x10, report[1]);
                Assert.Equal(2, report[currentId == 0x32 ? 10 : 13]);
            });
        Assert.False(_pairing.HasPending);

        Tick(3, hasSpeaker: false);
        Tick(4, hasSpeaker: false);
        Assert.Equal(new byte[] { 0x32, 0x32 }, _writes.Skip(2).Select(r => r[0]));
        Tick(5);
        Assert.Equal(4, _writes.Count);
    }

    [Fact]
    public void Single_writes_the_same_bytes_as_the_single_frame_builder_every_tick()
    {
        var expected = new HapticReport();
        Tick(1, mode: SpeakerPacket.Single36);
        Assert.Equal(expected.BuildSpeaker(_haptics, _opus), Assert.Single(_writes));
        Tick(2, hasSpeaker: false, mode: SpeakerPacket.Single36);
        Assert.Equal(expected.Build(_haptics), _writes[1]);
        Tick(3, mode: SpeakerPacket.Single36);
        Assert.Equal(expected.BuildSpeaker(_haptics, _opus), _writes[2]);
        Assert.False(_pairing.HasPending);
    }

    [Theory]
    [InlineData("{}", "single36")]
    [InlineData("{\"emulation\":{\"speakerPacket\":\"single36\"}}", "single36")]
    [InlineData("{\"emulation\":{\"speakerPacket\":\"invalid\"}}", "single36")]
    public void Speaker_packet_defaults_and_round_trips_as_camel_case(string json, string expected)
    {
        AppSettings settings = Validation.Normalize(JsonSerializer.Deserialize<AppSettings>(json, TactileJson.Compact)!);
        Assert.Contains($"\"speakerPacket\":\"{expected}\"", JsonSerializer.Serialize(settings, TactileJson.Compact));
    }

    private void Tick(byte marker, bool hasSpeaker = true, SpeakerPacket mode = SpeakerPacket.Double39)
    {
        Array.Fill(_haptics, (sbyte)marker);
        Array.Fill(_opus, marker);
        Assert.True(HapticStreamer.WriteTick(_builder, _pairing, mode, hasSpeaker, _haptics, _opus, report =>
        {
            _writes.Add(report.ToArray());
            return true;
        }));
    }
}
