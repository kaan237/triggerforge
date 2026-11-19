namespace Tactile.Emulation;

/// <summary>Frame scheduling for the two audio endpoints, driven under the connection's write lock.</summary>
internal sealed class IsoScheduler(
    Func<long> elapsedMilliseconds,
    VirtualDualSense.WithAudio device,
    Action<IsoSubmit, int> complete)
{
    private sealed class Pending(IsoSubmit submit, long firstFrame)
    {
        public IsoSubmit Submit { get; } = submit;
        public long FirstFrame { get; } = firstFrame;
        public int Packet { get; set; }
        public int Bytes { get; set; }
    }

    // Per endpoint; usbaudio.sys may keep many short URBs in flight.
    internal const int MaxQueuedPerEndpoint = 256;

    private readonly List<Pending>[] _endpoints = [[], []];
    private long _lastFrame;

    public bool TryEnqueue(IsoSubmit submit)
    {
        List<Pending> queue = _endpoints[submit.In ? 1 : 0];
        if (queue.Count >= MaxQueuedPerEndpoint)
        {
            return false;
        }

        queue.Add(new Pending(submit, elapsedMilliseconds() + 1));
        return true;
    }

    public bool Unlink(uint seqNum)
    {
        foreach (List<Pending> endpoint in _endpoints)
        {
            int index = endpoint.FindIndex(p => p.Submit.Header.SeqNum == seqNum);
            if (index >= 0)
            {
                endpoint.RemoveAt(index);
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs at most one frame and returns the milliseconds to wait before checking again.</summary>
    public int Tick()
    {
        long nextFrame = long.MaxValue;
        foreach (List<Pending> endpoint in _endpoints)
        {
            if (endpoint.Count > 0)
            {
                nextFrame = Math.Min(nextFrame, endpoint[0].FirstFrame);
            }
        }

        if (nextFrame == long.MaxValue)
        {
            return Timeout.Infinite;
        }

        nextFrame = Math.Max(nextFrame, _lastFrame + 1);
        long now = elapsedMilliseconds();
        if (nextFrame > now)
        {
            return (int)Math.Min(int.MaxValue, nextFrame - now);
        }

        _lastFrame = nextFrame;
        foreach (List<Pending> endpoint in _endpoints)
        {
            if (endpoint.Count > 0 && endpoint[0].FirstFrame <= nextFrame)
            {
                TransferPacket(endpoint);
            }
        }

        return 0;
    }

    private void TransferPacket(List<Pending> endpoint)
    {
        Pending pending = endpoint[0];
        IsoSubmit submit = pending.Submit;
        Span<byte> data = UsbIpCodec.IsoPacketBuffer(submit, pending.Packet++);
        if (submit.In)
        {
            data[device.AudioIn(data)..].Clear();
            pending.Bytes += data.Length;
        }
        else
        {
            device.AudioOut(data);
        }

        if (pending.Packet == submit.Header.Packets)
        {
            endpoint.RemoveAt(0);
            complete(submit, submit.In ? pending.Bytes : submit.Buffer.Length);
        }
    }
}
