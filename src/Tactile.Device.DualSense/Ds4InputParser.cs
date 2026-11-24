using System.Buffers.Binary;

namespace Tactile.Device.DualSense;

/// <summary>Unwraps the 16-bit DualShock 4 sensor clock (5.33 us ticks) into 32-bit 0.333 us ticks. One per controller.</summary>
public struct Ds4Timestamp
{
    private bool _started;
    private bool _hasPrevious;
    private ushort _previous;
    private uint _accumulated;

    public uint Next(ushort raw)
    {
        if (!_started)
        {
            _started = true;
            _accumulated = (uint)raw << 4;
        }
        else if (_hasPrevious)
        {
            _accumulated = unchecked(_accumulated + ((uint)(ushort)(raw - _previous) << 4));
        }

        _previous = raw;
        _hasPrevious = true;
        return _accumulated;
    }

    /// <summary>Forgets only the previous 16-bit sample (a new sensor clock follows); the accumulated clock keeps counting up.</summary>
    public void Restart() => _hasPrevious = false;
}

/// <summary>What the reader does with one wireless adaptor report.</summary>
public enum DongleStep
{
    /// <summary>Parse and publish it.</summary>
    Pass,
    /// <summary>The controller was just paired again: restart the clock, re-read calibration, then parse it.</summary>
    Resume,
    /// <summary>Drop it; no controller is paired.</summary>
    Drop,
    /// <summary>Drop it, but publish one neutral state first so nothing stays held.</summary>
    DropAndRelease,
}

/// <summary>
/// Follows the "no controller paired" flag of a wireless adaptor. A fresh tracker asks for a
/// <see cref="DongleStep.Resume"/> on the first paired report, so calibration is read once more there.
/// </summary>
public struct DonglePairing
{
    private bool _awaitingPair;
    private bool _gotInput;
    private bool _calibrationPending;
    private int _calibrationRounds;
    private int _reportsSinceTry;

    /// <summary>Reports between two calibration reads after a pairing.</summary>
    public const int CalibrationRetryReports = 250;
    /// <summary>Most calibration reads per pairing.</summary>
    public const int CalibrationMaxRounds = 8;

    public DonglePairing()
    {
        _awaitingPair = true;
    }

    public DongleStep Next(bool unpaired)
    {
        if (unpaired)
        {
            bool release = _gotInput;
            _gotInput = false;
            _awaitingPair = true;
            return release ? DongleStep.DropAndRelease : DongleStep.Drop;
        }

        bool resume = _awaitingPair;
        if (resume)
        {
            _calibrationPending = true;
            _calibrationRounds = 0;
            _reportsSinceTry = 0;
        }

        _awaitingPair = false;
        _gotInput = true;
        return resume ? DongleStep.Resume : DongleStep.Pass;
    }

    /// <summary>
    /// Call once per accepted report: true when calibration should be read now (right at the pairing, then about
    /// every <see cref="CalibrationRetryReports"/> reports, at most <see cref="CalibrationMaxRounds"/> times) until
    /// <see cref="CalibrationRead"/> reports success.
    /// </summary>
    public bool ShouldReadCalibration()
    {
        if (!_calibrationPending)
        {
            return false;
        }

        if (_calibrationRounds > 0 && ++_reportsSinceTry < CalibrationRetryReports)
        {
            return false;
        }

        _reportsSinceTry = 0;
        if (++_calibrationRounds >= CalibrationMaxRounds)
        {
            _calibrationPending = false;
        }

        return true;
    }

    /// <summary>A read found real calibration: stop retrying.</summary>
    public void CalibrationRead() => _calibrationPending = false;
}

/// <summary>Turns DualShock 4 input reports (USB 0x01, Bluetooth 0x11..0x19) into <see cref="InputState"/>.</summary>
public static class Ds4InputParser
{
    private const int UsbLength = 64;
    private const int BluetoothLength = 78;
    private const int TouchOffset = 34;

    private static readonly Buttons[] HatToButtons =
    [
        Buttons.Up, Buttons.Up | Buttons.Right, Buttons.Right, Buttons.Down | Buttons.Right,
        Buttons.Down, Buttons.Down | Buttons.Left, Buttons.Left, Buttons.Up | Buttons.Left,
    ];

    /// <summary>The wireless adaptor flags "no controller paired" in a USB report.</summary>
    public static bool DongleUnpaired(ReadOnlySpan<byte> report) => report.Length > 31 && (report[31] & 0x04) != 0;

    /// <summary>Everything released and centred; keeps the last battery and sensor clock.</summary>
    public static InputState Neutral(InputState? last) => new(
        Buttons.None,
        new Stick(128, 128),
        new Stick(128, 128),
        0,
        0,
        default,
        default,
        last?.SensorTimestamp ?? 0,
        default,
        default,
        last?.Battery ?? new Battery(0, ChargeState.Discharging),
        false,
        false,
        false);

    /// <param name="timestamp">Per-controller clock state; only advanced by accepted reports.</param>
    /// <param name="dongle">Report comes from the wireless adaptor: unpaired reports are dropped.</param>
    public static ParseResult TryParse(ReadOnlySpan<byte> report, Link link, ref Ds4Timestamp timestamp, out InputState? state, bool dongle = false)
    {
        state = null;
        ReadOnlySpan<byte> b;
        int maxTouch;
        if (link == Link.Usb)
        {
            if (report.Length < UsbLength || report[0] != 0x01)
            {
                return ParseResult.Unknown;
            }

            if (dongle && DongleUnpaired(report))
            {
                return ParseResult.Unknown;
            }

            b = report;
            maxTouch = 3;
        }
        else
        {
            if (report.Length > 0 && report[0] == 0x01)
            {
                return ParseResult.ReducedReport;
            }

            if (report.Length < BluetoothLength || report[0] < 0x11 || report[0] > 0x19 || (report[1] & 0x80) == 0)
            {
                return ParseResult.Unknown;
            }

            if (!Crc32.Verify(Crc32.InputSeed, report[..BluetoothLength]))
            {
                return ParseResult.BadCrc;
            }

            b = report[2..BluetoothLength];
            maxTouch = 4;
        }

        state = Read(b, maxTouch, ref timestamp);
        return ParseResult.Ok;
    }

    private static InputState Read(ReadOnlySpan<byte> b, int maxTouch, ref Ds4Timestamp timestamp)
    {
        Buttons buttons = Buttons.None;
        int hat = b[5] & 0x0F;
        if (hat < 8)
        {
            buttons |= HatToButtons[hat];
        }

        if ((b[5] & 0x10) != 0) buttons |= Buttons.Square;
        if ((b[5] & 0x20) != 0) buttons |= Buttons.Cross;
        if ((b[5] & 0x40) != 0) buttons |= Buttons.Circle;
        if ((b[5] & 0x80) != 0) buttons |= Buttons.Triangle;
        buttons |= (Buttons)((uint)b[6] << 4);
        if ((b[7] & 0x01) != 0) buttons |= Buttons.PS;
        if ((b[7] & 0x02) != 0) buttons |= Buttons.Touchpad;

        uint ticks = timestamp.Next(BinaryPrimitives.ReadUInt16LittleEndian(b[10..]));

        int count = Math.Min(Math.Min((int)b[33], maxTouch), (b.Length - TouchOffset) / 9);
        TouchPoint touch1 = default, touch2 = default;
        if (count >= 1)
        {
            ReadOnlySpan<byte> packet = b.Slice(TouchOffset + (count - 1) * 9, 9);
            touch1 = ReadTouch(packet.Slice(1, 4));
            touch2 = ReadTouch(packet.Slice(5, 4));
        }

        int status = b[30];
        return new InputState(
            buttons,
            new Stick(b[1], b[2]),
            new Stick(b[3], b[4]),
            b[8],
            b[9],
            ReadVector(b[13..]),
            ReadVector(b[19..]),
            ticks,
            touch1,
            touch2,
            ReadBattery(status & 0x0F, (status & 0x10) != 0),
            (status & 0x20) != 0,
            (status & 0x40) != 0,
            false);
    }

    private static Battery ReadBattery(int level, bool cable)
    {
        if (cable)
        {
            return level switch
            {
                < 10 => new Battery(level * 10 + 5, ChargeState.Charging),
                10 => new Battery(100, ChargeState.Charging),
                11 => new Battery(100, ChargeState.Full),
                _ => new Battery(0, ChargeState.Error),
            };
        }

        return new Battery(Math.Min(level * 10 + 5, 100), ChargeState.Discharging);
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> s) => new(
        BinaryPrimitives.ReadInt16LittleEndian(s),
        BinaryPrimitives.ReadInt16LittleEndian(s[2..]),
        BinaryPrimitives.ReadInt16LittleEndian(s[4..]));

    private static TouchPoint ReadTouch(ReadOnlySpan<byte> t) => new(
        (t[0] & 0x80) == 0,
        (byte)(t[0] & 0x7F),
        (ushort)(t[1] | ((t[2] & 0x0F) << 8)),
        (ushort)((t[2] >> 4) | (t[3] << 4)));
}
