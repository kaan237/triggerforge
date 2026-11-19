using Tactile.Device.DualSense;

namespace Tactile.Core.Integration;

internal sealed class LayerPublisher(Action<Func<OutputState, OutputState>?> setLayer)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    private readonly Lock _publishLock = new();
    private long _version;
    private UdpState _requested = UdpState.Empty;
    private UdpState _applied = UdpState.Empty;
    private TimeSpan? _lastPublish;

    public UdpState Applied => Volatile.Read(ref _applied);

    // The listener lock protects scheduling; the publish lock serializes output callbacks.
    public Publication? Prepare(UdpState state, TimeSpan time, bool immediate = false)
    {
        if (state == _requested || (!immediate && NextPublish is { } next && time < next))
        {
            return null;
        }

        _requested = state;
        _lastPublish = time;
        return new(Interlocked.Increment(ref _version), state);
    }

    public TimeSpan? NextPublish => _lastPublish + Interval;

    public bool HasPending(UdpState state) => state != _requested;

    public void Publish(Publication? publication, Lock listenerLock)
    {
        if (publication is not { } update)
        {
            return;
        }

        lock (_publishLock)
        {
            if (update.Version != Interlocked.Read(ref _version))
            {
                return;
            }

            try
            {
                setLayer(update.State == UdpState.Empty ? null : update.State.Overlay);
                Volatile.Write(ref _applied, update.State);
            }
            catch
            {
                lock (listenerLock)
                {
                    if (update.Version == _version)
                    {
                        _requested = _applied;
                    }
                }

                throw;
            }
        }
    }

    internal readonly record struct Publication(long Version, UdpState State);
}
