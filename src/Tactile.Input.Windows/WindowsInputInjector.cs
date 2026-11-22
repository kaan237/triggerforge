using static Tactile.Input.Windows.Native;
using Buttons = Tactile.Input.MouseButton;
using K = Tactile.Input.Key;

namespace Tactile.Input.Windows;

/// <summary>Keyboard and mouse output through SendInput. Safe from any thread.</summary>
public sealed class WindowsInputInjector : IInputInjector
{
    /// <summary>Put in every event's extra-info field, so our own input can be told apart later.</summary>
    public const uint Signature = 0x54414354;

    private static readonly unsafe int InputSize = sizeof(InputRecord);

    public void Key(Key key, bool down)
    {
        if (key == K.None)
        {
            return;
        }

        var k = new KeybdInput { ExtraInfo = Signature };
        ushort vk = VirtualKey(key);
        if (vk != 0)
        {
            // Windows acts on the virtual key for these, not on the scan code.
            k.Vk = vk;
            k.Flags = KeyInfo.IsExtended(key) ? KeyeventfExtendedKey : 0;
        }
        else
        {
            k.Scan = (ushort)((ushort)key & 0xFF);
            k.Flags = KeyeventfScanCode | (KeyInfo.IsExtended(key) ? KeyeventfExtendedKey : 0);
        }

        if (!down)
        {
            k.Flags |= KeyeventfKeyUp;
        }

        var input = new InputRecord { Type = InputKeyboard };
        input.U.Keyboard = k;
        Send(input);
    }

    public void MouseButton(MouseButton button, bool down)
    {
        (uint flags, uint data) = button switch
        {
            Buttons.Left => (down ? MouseeventfLeftDown : MouseeventfLeftUp, 0u),
            Buttons.Right => (down ? MouseeventfRightDown : MouseeventfRightUp, 0u),
            Buttons.Middle => (down ? MouseeventfMiddleDown : MouseeventfMiddleUp, 0u),
            Buttons.X1 => (down ? MouseeventfXDown : MouseeventfXUp, XButton1),
            Buttons.X2 => (down ? MouseeventfXDown : MouseeventfXUp, XButton2),
            _ => (0u, 0u),
        };
        if (flags != 0)
        {
            SendMouse(0, 0, data, flags);
        }
    }

    public void MouseMove(int dx, int dy)
    {
        if (dx != 0 || dy != 0)
        {
            SendMouse(dx, dy, 0, MouseeventfMove);
        }
    }

    public void Wheel(int delta, bool horizontal)
    {
        if (delta != 0)
        {
            // mouseData is a DWORD but carries a signed amount.
            SendMouse(0, 0, unchecked((uint)delta), horizontal ? MouseeventfHWheel : MouseeventfWheel);
        }
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new InputRecord { Type = InputMouse };
        input.U.Mouse = new MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags, ExtraInfo = Signature };
        Send(input);
    }

    private static unsafe void Send(InputRecord input) => SendInput(1, &input, InputSize);

    public static ushort VirtualKey(Key key) => key switch
    {
        K.MediaPrevious => 0xB1,
        K.MediaNext => 0xB0,
        K.MediaStop => 0xB2,
        K.MediaPlayPause => 0xB3,
        K.VolumeMute => 0xAD,
        K.VolumeDown => 0xAE,
        K.VolumeUp => 0xAF,
        K.PrintScreen => 0x2C,
        _ => 0,
    };
}
