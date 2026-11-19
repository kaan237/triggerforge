using Tactile.Emulation;

namespace Tactile.Tests.Emulation;

public class IsoSchedulerTests
{
    [Fact]
    public void One_packet_per_endpoint_per_frame_with_fifo_completion()
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense.WithAudio)server.CreateDualSense(null, audio: true);
        long now = 0;
        var played = new List<short>();
        var completed = new List<uint>();
        pad.Audio += data => played.Add(data[0]);
        var scheduler = new IsoScheduler(() => now, pad, (submit, _) => completed.Add(submit.Header.SeqNum));
        scheduler.TryEnqueue(Create(1, false, 3, 10));
        scheduler.TryEnqueue(Create(2, false, 1, 20));
        scheduler.TryEnqueue(Create(3, true, 2));

        Assert.Equal(1, scheduler.Tick());
        now = 1;
        Assert.Equal(0, scheduler.Tick());
        Assert.Equal(1, scheduler.Tick());
        Assert.Equal([10], played);
        Assert.Empty(completed);

        now = 2;
        Assert.Equal(0, scheduler.Tick());
        Assert.Equal([10, 11], played);
        Assert.Equal([3u], completed);

        now = 3;
        Assert.Equal(0, scheduler.Tick());
        Assert.Equal(1, scheduler.Tick());
        Assert.Equal([10, 11, 12], played);
        Assert.Equal([3u, 1u], completed);

        now = 4;
        Assert.Equal(0, scheduler.Tick());
        Assert.Equal([10, 11, 12, 20], played);
        Assert.Equal([3u, 1u, 2u], completed);
    }

    [Fact]
    public void Late_tick_catches_up_without_replaying_idle_or_prearrival_frames()
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense.WithAudio)server.CreateDualSense(null, audio: true);
        long now = 0;
        var completed = new List<uint>();
        var scheduler = new IsoScheduler(() => now, pad, (submit, _) => completed.Add(submit.Header.SeqNum));
        scheduler.TryEnqueue(Create(1, false, 3));
        now = 4;
        scheduler.TryEnqueue(Create(2, false, 1));
        scheduler.TryEnqueue(Create(3, true, 1));

        Assert.Equal(0, scheduler.Tick());
        Assert.Equal(0, scheduler.Tick());
        Assert.Empty(completed);
        Assert.Equal(0, scheduler.Tick());
        Assert.Equal([1u], completed);
        Assert.Equal(1, scheduler.Tick());

        now = 5;
        Assert.Equal(0, scheduler.Tick());
        Assert.Equal([1u, 2u, 3u], completed);
        Assert.Equal(Timeout.Infinite, scheduler.Tick());

        now = 10_000;
        scheduler.TryEnqueue(Create(4, true, 2));
        Assert.Equal(1, scheduler.Tick());
        now++;
        Assert.Equal(0, scheduler.Tick());
        Assert.Equal(1, scheduler.Tick());
        Assert.Equal([1u, 2u, 3u], completed);
        now++;
        Assert.Equal(0, scheduler.Tick());
        Assert.Equal([1u, 2u, 3u, 4u], completed);
    }

    [Fact]
    public void Unlink_removes_both_partially_processed_and_waiting_urbs()
    {
        using var server = new UsbIpServer(0);
        var pad = (VirtualDualSense.WithAudio)server.CreateDualSense(null, audio: true);
        long now = 0;
        var completed = new List<uint>();
        var played = new List<short>();
        pad.Audio += data => played.Add(data[0]);
        var scheduler = new IsoScheduler(() => now, pad, (submit, _) => completed.Add(submit.Header.SeqNum));
        scheduler.TryEnqueue(Create(1, false, 3, 10));
        scheduler.TryEnqueue(Create(2, false, 1, 20));
        scheduler.TryEnqueue(Create(3, false, 1, 30));
        now = 1;
        Assert.Equal(0, scheduler.Tick());

        Assert.True(scheduler.Unlink(2));
        Assert.True(scheduler.Unlink(1));
        Assert.False(scheduler.Unlink(1));
        now = 10;
        Assert.Equal(0, scheduler.Tick());
        Assert.Equal(Timeout.Infinite, scheduler.Tick());
        Assert.Equal([10, 30], played);
        Assert.Equal([3u], completed);
    }

    private static IsoSubmit Create(uint seq, bool input, int count, byte sample = 0)
    {
        const int packetSize = 8;
        byte[] buffer = new byte[count * packetSize];
        var packets = new (uint, uint)[count];
        for (int i = 0; i < count; i++)
        {
            if (!input)
            {
                buffer[i * packetSize] = (byte)(sample + i);
            }

            packets[i] = ((uint)(i * packetSize), packetSize);
        }

        var header = new UrbHeader(1, seq, 0, input ? 1u : 0u, input ? 2u : 1u, 0, buffer.Length, 2000, count, 1, 0);
        return new IsoSubmit(header, buffer, FakeUsbIpClient.IsoDescriptors(packets));
    }
}
