using System.Net;
using System.Net.Sockets;

namespace Tactile.Core.Integration;

internal sealed class ListenerWait
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _pending;

    public CancellationTokenSource Begin()
    {
        lock (_lock)
        {
            _pending = new CancellationTokenSource();
            return _pending;
        }
    }

    public void Wake()
    {
        lock (_lock)
        {
            _pending?.Cancel();
        }
    }

    public SocketReceiveFromResult? Receive(Socket? socket, Memory<byte> buffer, int milliseconds,
        CancellationTokenSource wait)
    {
        try
        {
            if (socket is null)
            {
                wait.Token.WaitHandle.WaitOne(milliseconds);
                return null;
            }

            wait.CancelAfter(milliseconds);
            EndPoint sender = new IPEndPoint(IPAddress.Loopback, 0);
            return socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, wait.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_lock)
            {
                _pending = null;
                wait.Dispose();
            }
        }
    }

    public static int Until(TimeSpan time, TimeSpan? deadline) => deadline is { } next
        ? (int)Math.Clamp(Math.Ceiling((next - time).TotalMilliseconds), 0, int.MaxValue)
        : Timeout.Infinite;
}
