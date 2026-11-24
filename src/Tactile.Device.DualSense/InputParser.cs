using System.Buffers.Binary;

namespace Tactile.Device.DualSense;

public enum ParseResult { Ok, ReducedReport, BadCrc, Unknown, MicAudio }

/// <summary>Turns raw input reports into <see cref="InputState"/>. Layout: docs/protocol.md.</summary>
public static class InputParser
{
    public const int StateLength = 63;

    // Hat values 0-7 clockwise from up; 8 = released.
    private static readonly Buttons[] HatToButtons =
    [
        Buttons.Up, Buttons.Up | Buttons.Right, Buttons.Right, Buttons.Down | Buttons.Right,
        Buttons.Down, Buttons.Down | Buttons.Left, Buttons.Left, Buttons.Up | Buttons.Left,
    ];

    /// <param name="micStream">The controller's microphone stream is on; only then may a 0x31 report with bit 1 set be a microphone frame.</param>
    public static ParseResult TryParse(ReadOnlySpan<byte> report, Link link, out InputState? state, bool micStream = false)
    {
        state = null;
        ReadOnlySpan<byte> block;
        if (link == Link.Usb)
        {
            if (report.Length < 1 + StateLength || report[0] != 0x01)
            {
                return ParseResult.Unknown;
            }

            block = report.Slice(1, StateLength);
        }
        else
        {
            if (report.Length > 0 && report[0] == 0x01)
            {
                return ParseResult.ReducedReport;
            }

            if (report.Length < DualSenseIds.BluetoothInputReportLength || report[0] != 0x31)
            {
                return ParseResult.Unknown;
            }

            // Microphone frames replace the state block and carry no valid CRC: split them off first.
            // With the stream off the bit is ignored, so input handling is exactly what it was before the feature.
            if (micStream && (report[1] & 0x02) != 0)
            {
                return ParseResult.MicAudio;
            }

            if (!Crc32.Verify(Crc32.InputSeed, report[..DualSenseIds.BluetoothInputReportLength]))
            {
                return ParseResult.BadCrc;
            }

            block = report.Slice(2, StateLength);
        }

        state = ParseState(block);
        return ParseResult.Ok;
    }

    public const int MicPacketLength = 71;

    /// <summary>Extracts the 8-bit sequence counter and the 71-byte Opus frame of a Bluetooth microphone report.</summary>
    public static bool TryParseMic(ReadOnlySpan<byte> report, out byte sequence, out ReadOnlySpan<byte> opus71)
    {
        sequence = 0;
        opus71 = default;
        if (report.Length < DualSenseIds.BluetoothInputReportLength || report[0] != 0x31 || (report[1] & 0x02) == 0)
        {
            return false;
        }

        sequence = report[2];
        opus71 = report.Slice(3, MicPacketLength);
        return true;
    }

    public static InputState ParseState(ReadOnlySpan<byte> s)
    {
        return new InputState(
            ReadButtons(s),
            new Stick(s[0], s[1]),
            new Stick(s[2], s[3]),
            s[4],
            s[5],
            ReadVector(s[15..]),
            ReadVector(s[21..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[27..]),
            ReadTouch(s.Slice(32, 4)),
            ReadTouch(s.Slice(36, 4)),
            ReadBattery(s[52]),
            (s[53] & 0x01) != 0,
            (s[53] & 0x02) != 0,
            (s[53] & 0x04) != 0);
    }

    private static Buttons ReadButtons(ReadOnlySpan<byte> s)
    {
        Buttons b = Buttons.None;
        int hat = s[7] & 0x0F;
        if (hat < 8)
        {
            b |= HatToButtons[hat];
        }

        if ((s[7] & 0x10) != 0) b |= Buttons.Square;
        if ((s[7] & 0x20) != 0) b |= Buttons.Cross;
        if ((s[7] & 0x40) != 0) b |= Buttons.Circle;
        if ((s[7] & 0x80) != 0) b |= Buttons.Triangle;

        // Offset 8 maps 1:1 onto L1..R3.
        b |= (Buttons)((uint)s[8] << 4);

        if ((s[9] & 0x01) != 0) b |= Buttons.PS;
        if ((s[9] & 0x02) != 0) b |= Buttons.Touchpad;
        if ((s[9] & 0x04) != 0) b |= Buttons.Mic;
        if ((s[9] & 0x10) != 0) b |= Buttons.Fn1;
        if ((s[9] & 0x20) != 0) b |= Buttons.Fn2;
        if ((s[9] & 0x40) != 0) b |= Buttons.PaddleLeft;
        if ((s[9] & 0x80) != 0) b |= Buttons.PaddleRight;
        return b;
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

    private static Battery ReadBattery(byte status)
    {
        int level = status & 0x0F;
        var charge = (ChargeState)(status >> 4);
        // 0-10 in steps of 10 %; report as 5 % past the step while discharging.
        int percent = charge == ChargeState.Full ? 100 : Math.Min(level * 10 + 5, 100);
        return new Battery(percent, charge);
    }
}
