using System.Collections.Concurrent;

namespace Tactile.Core;

/// <summary>
/// Runs slow actions (programs, profile switches, microphone, macro start/stop) in order on
/// one background thread, so the HID reader never waits on them. In manual mode jobs wait
/// until <see cref="RunPending"/>, which the mapper calls once it has let go of its lock;
/// tests use that to stay deterministic.
/// </summary>
internal sealed class ActionWorker : IDisposable
{
    private readonly bool _threaded;
    private readonly Action<Exception> _onError;
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly Lock _startLock = new();
    private BlockingCollection<Action>? _queue;
    private volatile bool _disposed;

    public ActionWorker(bool threaded, Action<Exception> onError)
    {
        _threaded = threaded;
        _onError = onError;
    }

    public void Post(Action job)
    {
        if (_disposed)
        {
            return;
        }

        if (!_threaded)
        {
            _pending.Enqueue(job);
            return;
        }

        BlockingCollection<Action> queue = EnsureThread();
        try
        {
            queue.Add(job);
        }
        catch (InvalidOperationException)
        {
            // Disposed meanwhile.
        }
    }

    /// <summary>Manual mode: runs queued jobs on the calling thread. No-op when threaded.</summary>
    public void RunPending()
    {
        while (!_disposed && _pending.TryDequeue(out Action? job))
        {
            Run(job);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_startLock)
        {
            _queue?.CompleteAdding();
        }
    }

    private BlockingCollection<Action> EnsureThread()
    {
        lock (_startLock)
        {
            if (_queue is null)
            {
                _queue = new BlockingCollection<Action>();
                BlockingCollection<Action> q = _queue;
                new Thread(() => Loop(q)) { IsBackground = true, Name = "Tactile mapping actions" }.Start();
            }

            return _queue;
        }
    }

    private void Loop(BlockingCollection<Action> queue)
    {
        foreach (Action job in queue.GetConsumingEnumerable())
        {
            if (_disposed)
            {
                break;
            }

            Run(job);
        }
    }

    private void Run(Action job)
    {
        try
        {
            job();
        }
        catch (Exception e)
        {
            _onError(e);
        }
    }
}
