using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace Tactile.Core.Integration;

public enum AcGame { Ac, Acc }

internal readonly record struct AcTelemetryFrame(bool IsAcc, TelemetryValues Values, double RevLights = 0, int Drs = 0);

internal interface ISharedMemorySource : IDisposable
{
    bool TryRead(Span<byte> physics, out int physicsLength, Span<byte> graphics, out int graphicsLength, Span<byte> statics, out int staticsLength);
    void Close();
}

// Previous suspension travel, kept between frames to detect kerbs and bumps.
internal struct AcSuspensionState
{
    public bool HasPrevious;
    public float Fl, Fr, Rl, Rr;
}

internal static class AcSharedMemory
{
    public const string PhysicsMapName = @"Local\acpmf_physics";
    public const string GraphicsMapName = @"Local\acpmf_graphics";
    public const string StaticMapName = @"Local\acpmf_static";
    public const int MinPhysicsSize = 368;
    public const int AcPhysicsSize = 712;
    public const int MinGraphicsSize = 8;
    public const int MinStaticSize = 456;
    public const int AcStatusLive = 2;
    internal const double DefaultMaxRpm = 8000;

    public static bool TryParse(
        ReadOnlySpan<byte> physics, ReadOnlySpan<byte> graphics, ReadOnlySpan<byte> statics,
        ref AcSuspensionState state, out int packetId, out int status, out AcTelemetryFrame frame)
    {
        packetId = 0;
        status = 0;
        frame = default;
        if (!HasMinimum(physics, graphics, statics, MinPhysicsSize))
        {
            return false;
        }

        packetId = IntAt(physics, 0);
        status = IntAt(graphics, 4);
        double maxRpm = IntAt(statics, 452);
        if (maxRpm <= 0)
        {
            maxRpm = DefaultMaxRpm;
        }

        Common(physics, maxRpm, out double speed, out double rpm, out double gas, out double brake,
            out double clutch, out int gear, out double steer, out double slipFront, out double slipRear,
            out double boost);

        float fl = (float)FloatAt(physics, 184), fr = (float)FloatAt(physics, 188);
        float rl = (float)FloatAt(physics, 192), rr = (float)FloatAt(physics, 196);
        double delta = 0;
        if (state.HasPrevious)
        {
            delta = Math.Max(Math.Max(Math.Abs(fl - state.Fl), Math.Abs(fr - state.Fr)),
                Math.Max(Math.Abs(rl - state.Rl), Math.Abs(rr - state.Rr))) * 100.0;
        }

        state = new AcSuspensionState { HasPrevious = true, Fl = fl, Fr = fr, Rl = rl, Rr = rr };
        double surface = Math.Clamp(Math.Max(delta, Math.Abs(FloatAt(physics, 48) - 1.0) / 2.0), 0, 1);
        int drs = FloatAt(physics, 200) > 0.5 ? 1 : 0;
        int abs = FloatAt(physics, 252) > 0 || (brake > 0.1 && slipFront > 1.0 && speed > 5) ? 1 : 0;
        int traction = FloatAt(physics, 204) > 0 || (gas > 0.1 && slipRear > 1.0 && speed > 5) ? 1 : 0;

        frame = new AcTelemetryFrame(false, new TelemetryValues(
            status == AcStatusLive ? 1 : 0, speed, rpm, gas, brake, clutch, 0, gear, steer,
            slipFront, slipRear, surface, surface >= 0.45 ? 1 : 0, boost, abs, traction), rpm, drs);
        return true;
    }

    internal static bool HasMinimum(ReadOnlySpan<byte> physics, ReadOnlySpan<byte> graphics, ReadOnlySpan<byte> statics, int physicsMin) =>
        physics.Length >= physicsMin && graphics.Length >= MinGraphicsSize && statics.Length >= MinStaticSize;

    // Fields shared by both games. Floats are sanitized and clamped.
    internal static void Common(
        ReadOnlySpan<byte> physics, double maxRpm, out double speed, out double rpm, out double gas,
        out double brake, out double clutch, out int gear, out double steer, out double slipFront,
        out double slipRear, out double boost)
    {
        gas = Math.Clamp(FloatAt(physics, 4), 0, 1);
        brake = Math.Clamp(FloatAt(physics, 8), 0, 1);
        gear = Math.Max(0, IntAt(physics, 16) - 1);
        rpm = Math.Clamp(IntAt(physics, 20) / maxRpm, 0, 1);
        steer = Math.Clamp(FloatAt(physics, 24), -1, 1);
        speed = Math.Max(0, FloatAt(physics, 28));
        slipFront = Math.Max(Math.Abs(FloatAt(physics, 56)), Math.Abs(FloatAt(physics, 60)));
        slipRear = Math.Max(Math.Abs(FloatAt(physics, 64)), Math.Abs(FloatAt(physics, 68)));
        boost = FloatAt(physics, 276);
        clutch = Math.Clamp(FloatAt(physics, 364), 0, 1);
    }

    internal static int IntAt(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);

    internal static double FloatAt(ReadOnlySpan<byte> data, int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
        return float.IsFinite(value) ? value : 0;
    }
}

// Opens the three maps read-only on demand. Never creates or writes shared memory.
internal sealed class WindowsSharedMemorySource : ISharedMemorySource
{
    private MemoryMappedFile? _physicsFile, _graphicsFile, _staticFile;
    private MemoryMappedViewAccessor? _physicsView, _graphicsView, _staticView;

    public bool TryRead(Span<byte> physics, out int physicsLength, Span<byte> graphics, out int graphicsLength, Span<byte> statics, out int staticsLength)
    {
        physicsLength = graphicsLength = staticsLength = 0;
        try
        {
            if (_physicsView is null || _graphicsView is null || _staticView is null)
            {
                Open();
            }

            physicsLength = Copy(_physicsView!, physics);
            graphicsLength = Copy(_graphicsView!, graphics);
            staticsLength = Copy(_staticView!, statics);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException
            or ArgumentException or InvalidOperationException)
        {
            Close();
            physicsLength = graphicsLength = staticsLength = 0;
            return false;
        }
    }

    public void Close()
    {
        _physicsView?.Dispose();
        _graphicsView?.Dispose();
        _staticView?.Dispose();
        _physicsFile?.Dispose();
        _graphicsFile?.Dispose();
        _staticFile?.Dispose();
        _physicsView = _graphicsView = _staticView = null;
        _physicsFile = _graphicsFile = _staticFile = null;
    }

    public void Dispose() => Close();

    private void Open()
    {
        _physicsFile = MemoryMappedFile.OpenExisting(AcSharedMemory.PhysicsMapName, MemoryMappedFileRights.Read);
        _graphicsFile = MemoryMappedFile.OpenExisting(AcSharedMemory.GraphicsMapName, MemoryMappedFileRights.Read);
        _staticFile = MemoryMappedFile.OpenExisting(AcSharedMemory.StaticMapName, MemoryMappedFileRights.Read);
        _physicsView = _physicsFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _graphicsView = _graphicsFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _staticView = _staticFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    // Reads at most the view capacity and the destination size.
    private static int Copy(MemoryMappedViewAccessor view, Span<byte> destination)
    {
        int length = (int)Math.Min(view.Capacity, destination.Length);
        byte[] temp = new byte[length];
        view.ReadArray(0, temp, 0, length);
        temp.CopyTo(destination);
        return length;
    }
}
