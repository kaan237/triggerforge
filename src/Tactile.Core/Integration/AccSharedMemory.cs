using System.Text;

namespace Tactile.Core.Integration;

internal static class AccSharedMemory
{
    public const int AccMinPhysicsSize = 800;
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(16);
    public static readonly TimeSpan StaleTimeout = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan OpenRetryInterval = TimeSpan.FromMilliseconds(500);

    public static bool TryParse(
        ReadOnlySpan<byte> physics, ReadOnlySpan<byte> graphics, ReadOnlySpan<byte> statics,
        out int packetId, out int status, out AcTelemetryFrame frame)
    {
        packetId = 0;
        status = 0;
        frame = default;
        if (!AcSharedMemory.HasMinimum(physics, graphics, statics, AccMinPhysicsSize))
        {
            return false;
        }

        packetId = AcSharedMemory.IntAt(physics, 0);
        status = AcSharedMemory.IntAt(graphics, 4);
        double maxRpm = AcSharedMemory.IntAt(physics, 588);
        if (maxRpm <= 0)
        {
            maxRpm = AcSharedMemory.IntAt(statics, 452);
        }

        if (maxRpm <= 0)
        {
            maxRpm = AcSharedMemory.DefaultMaxRpm;
        }

        AcSharedMemory.Common(physics, maxRpm, out double speed, out double rpm, out double gas,
            out double brake, out double clutch, out int gear, out double steer, out double slipFront,
            out double slipRear, out double boost);

        double kerb = AcSharedMemory.FloatAt(physics, 784);
        double slipVibration = AcSharedMemory.FloatAt(physics, 788);
        double gVibration = AcSharedMemory.FloatAt(physics, 792);
        double absVibration = AcSharedMemory.FloatAt(physics, 796);
        int abs = AcSharedMemory.IntAt(physics, 676) != 0 || AcSharedMemory.FloatAt(physics, 252) > 0 || absVibration > 0.05 ? 1 : 0;
        int traction = AcSharedMemory.IntAt(physics, 672) != 0 || AcSharedMemory.FloatAt(physics, 204) > 0 ? 1 : 0;
        double surface = Math.Clamp(Math.Max(kerb, Math.Max(slipVibration, gVibration)), 0, 1);

        frame = new AcTelemetryFrame(true, new TelemetryValues(
            status == AcSharedMemory.AcStatusLive ? 1 : 0, speed, rpm, gas, brake, clutch, 0, gear, steer,
            slipFront, slipRear, surface, kerb > 0.05 ? 1 : 0, boost, abs, traction), rpm, 0);
        return true;
    }

    // Mapped views are page-rounded, so a 712-byte original AC map can look much larger with
    // zero padding, and its own fields at 588/672/676 are non-zero. Only bytes past the original
    // layout (712..799, which covers 780 and 784..796) count as evidence, besides the version text.
    public static bool IsAccBuffer(ReadOnlySpan<byte> physics, ReadOnlySpan<byte> statics)
    {
        if (physics.Length < AccMinPhysicsSize)
        {
            return false;
        }

        if (VersionLooksLikeAcc(statics))
        {
            return true;
        }

        return physics[AcSharedMemory.AcPhysicsSize..AccMinPhysicsSize].ContainsAnyExcept((byte)0);
    }

    private static bool VersionLooksLikeAcc(ReadOnlySpan<byte> statics)
    {
        int length = Math.Min(statics.Length, 60) / 2 * 2;
        if (length == 0)
        {
            return false;
        }

        string text = Encoding.Unicode.GetString(statics[..length]);
        return text.Contains("1.8", StringComparison.Ordinal) || text.Contains("ACC", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AccSharedMemoryReader(Func<TimeSpan> now, ISharedMemorySource? source = null) : IDisposable
{
    private const int BufferSize = 4096;

    private readonly bool _ownsSource = source is null;
    private readonly ISharedMemorySource _source = source ?? new WindowsSharedMemorySource();
    private readonly byte[] _physics = new byte[BufferSize];
    private readonly byte[] _graphics = new byte[BufferSize];
    private readonly byte[] _statics = new byte[BufferSize];
    private AcSuspensionState _suspension;
    private AcTelemetryFrame? _lastGood;
    private AcTelemetryFrame? _lastAc;
    private TimeSpan? _lastPoll;
    private TimeSpan _nextOpen;
    private int? _lastPacketId;
    private TimeSpan _lastPacketChange;
    private bool _reportedOn;
    private bool _disposed;

    public bool TryPoll(out AcTelemetryFrame frame, out bool changed)
    {
        frame = default;
        changed = false;
        if (_disposed)
        {
            return false;
        }

        TimeSpan t = now();
        if (_lastPoll is { } last && t - last < AccSharedMemory.MinPollInterval)
        {
            return false;
        }

        _lastPoll = t;
        if (t < _nextOpen)
        {
            return false;
        }

        Array.Clear(_physics);
        Array.Clear(_graphics);
        Array.Clear(_statics);
        int physicsLength, graphicsLength, staticsLength;
        try
        {
            if (!_source.TryRead(_physics, out physicsLength, _graphics, out graphicsLength, _statics, out staticsLength))
            {
                CloseSource(t);
                return EndSession(out frame);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same as the game being closed.
            CloseSource(t);
            return EndSession(out frame);
        }

        ReadOnlySpan<byte> physics = _physics.AsSpan(0, Math.Clamp(physicsLength, 0, BufferSize));
        ReadOnlySpan<byte> graphics = _graphics.AsSpan(0, Math.Clamp(graphicsLength, 0, BufferSize));
        ReadOnlySpan<byte> statics = _statics.AsSpan(0, Math.Clamp(staticsLength, 0, BufferSize));

        int packetId, status;
        AcTelemetryFrame parsed;
        bool isAcc = AccSharedMemory.IsAccBuffer(physics, statics);
        AcSuspensionState suspension = _suspension;
        bool ok = isAcc
            ? AccSharedMemory.TryParse(physics, graphics, statics, out packetId, out status, out parsed)
            : AcSharedMemory.TryParse(physics, graphics, statics, ref suspension, out packetId, out status, out parsed);
        if (!ok)
        {
            CloseSource(t);
            return EndSession(out frame);
        }

        bool repeated = _lastPacketId == packetId;
        if (!isAcc)
        {
            if (repeated && _lastAc is { } previous)
            {
                // The same physics step again: keep the last frame, so a kerb is not cut short by a zero delta.
                parsed = previous with { Values = previous.Values with { RaceOn = parsed.Values.RaceOn } };
            }
            else
            {
                _suspension = suspension;
                _lastAc = parsed;
            }
        }

        // The last packet id survives a reopen, so a frozen buffer is never fresh again.
        if (!repeated)
        {
            _lastPacketId = packetId;
            _lastPacketChange = t;
            changed = true;
        }

        _lastGood = parsed;
        if (status == AcSharedMemory.AcStatusLive && t - _lastPacketChange <= AccSharedMemory.StaleTimeout)
        {
            _reportedOn = true;
            frame = parsed;
            return true;
        }

        CloseSource(t);
        changed = false;
        return EndSession(out frame);
    }

    /// <summary>How long the caller can wait before the next poll can do anything.</summary>
    public TimeSpan NextWait()
    {
        TimeSpan t = now();
        TimeSpan due = (_lastPoll ?? t) + AccSharedMemory.MinPollInterval;
        if (_nextOpen > due)
        {
            due = _nextOpen;
        }

        return due > t ? due - t : TimeSpan.Zero;
    }

    // One RaceOn 0 frame ends a session that was reported on; nothing when it never was.
    private bool EndSession(out AcTelemetryFrame frame)
    {
        frame = default;
        if (!_reportedOn || _lastGood is not { } last)
        {
            return false;
        }

        _reportedOn = false;
        frame = last with { Values = last.Values with { RaceOn = 0 } };
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsSource)
        {
            _source.Dispose();
        }
        else
        {
            _source.Close();
        }
    }

    private void CloseSource(TimeSpan t)
    {
        _source.Close();
        _suspension = default;
        _nextOpen = t + AccSharedMemory.OpenRetryInterval;
    }
}
