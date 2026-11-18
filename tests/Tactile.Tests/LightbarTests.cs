using Tactile.Core;
using Tactile.Device.DualSense;

namespace Tactile.Tests;

public class LightbarTests
{
    private static readonly Rgb Blue = new(0x30, 0x60, 0xFF);

    private static Rgb At(LightbarConfig c, double t, int? battery = 50, int player = 1) =>
        LightbarEngine.Evaluate(c, t, battery, player).Color;

    [Fact]
    public void Off_and_static()
    {
        Assert.Equal(default, At(new LightbarConfig { Effect = LightbarEffect.Off }, 3));
        Assert.Equal(Blue, At(new LightbarConfig { Effect = LightbarEffect.Static }, 3));
        Assert.Equal(LedBrightness.Low, LightbarEngine.Evaluate(new LightbarConfig { Brightness = LedBrightness.Low }, 0, null, 1).Brightness);
    }

    [Fact]
    public void Breathe_goes_from_color_to_color2_and_back()
    {
        var c = new LightbarConfig { Effect = LightbarEffect.Breathe, Color = "#FF0000", Color2 = "#0000FF", Speed = 1 };
        // 0.5 cycles/s at speed 1: color at 0 s, color2 at 1 s, halfway at 0.5 s.
        Assert.Equal(new Rgb(255, 0, 0), At(c, 0));
        Assert.Equal(new Rgb(0, 0, 255), At(c, 1));
        Rgb mid = At(c, 0.5);
        Assert.InRange(mid.R, 127, 128);
        Assert.InRange(mid.B, 127, 128);
        Assert.Equal(new Rgb(255, 0, 0), At(c, 2));
        // Double speed reaches color2 twice as fast.
        Assert.Equal(new Rgb(0, 0, 255), At(c with { Speed = 2 }, 0.5));
    }

    [Fact]
    public void Strobe_is_a_square_wave()
    {
        var c = new LightbarConfig { Effect = LightbarEffect.Strobe, Color = "#FFFFFF", Color2 = "#000000" };
        Assert.Equal(new Rgb(255, 255, 255), At(c, 0.1));
        Assert.Equal(new Rgb(0, 0, 0), At(c, 0.3));
        Assert.Equal(new Rgb(255, 255, 255), At(c, 0.55));
    }

    [Fact]
    public void Rainbow_cycles_hue()
    {
        var c = new LightbarConfig { Effect = LightbarEffect.Rainbow };
        Assert.Equal(new Rgb(255, 0, 0), At(c, 0));
        Assert.Equal(new Rgb(0, 255, 0), At(c, 10.0 / 3));   // 120 degrees at 0.1 cycles/s
        Assert.Equal(new Rgb(0, 0, 255), At(c, 20.0 / 3));
        Assert.Equal(new Rgb(255, 0, 0), At(c, 10));
    }

    [Fact]
    public void Battery_colors()
    {
        var c = new LightbarConfig { Effect = LightbarEffect.Battery };
        Assert.Equal(new Rgb(0, 255, 0), At(c, 0, 100));
        Assert.Equal(new Rgb(0, 255, 0), At(c, 0, 80));
        Assert.Equal(new Rgb(255, 255, 0), At(c, 0, 45));
        Assert.Equal(new Rgb(255, 0, 0), At(c, 0, 10));
        // Pulses under 10 %.
        Assert.Equal(new Rgb(255, 0, 0), At(c, 0, 5));
        Assert.Equal(new Rgb(40, 0, 0), At(c, 0.5, 5));
        Assert.Equal(Blue, At(c, 0, null));
    }

    [Fact]
    public void Player_colors()
    {
        var c = new LightbarConfig { Effect = LightbarEffect.PlayerColor };
        Assert.Equal(new Rgb(0x00, 0x40, 0xFF), At(c, 0, player: 1));
        Assert.Equal(new Rgb(0xFF, 0x00, 0x00), At(c, 0, player: 2));
        Assert.Equal(new Rgb(0x00, 0xFF, 0x00), At(c, 0, player: 3));
        Assert.Equal(new Rgb(0xFF, 0x40, 0xC0), At(c, 0, player: 4));
    }

    [Fact]
    public void Player_led_patterns()
    {
        Assert.Equal(0b00100, PlayerLeds.ForNumber(1));
        Assert.Equal(0b01010, PlayerLeds.ForNumber(2));
        Assert.Equal(0b10101, PlayerLeds.ForNumber(3));
        Assert.Equal(0b11011, PlayerLeds.ForNumber(4));
        Assert.Equal(0, PlayerLeds.Mask(new PlayerLedConfig { Mode = PlayerLedMode.Off, Number = 2 }));
        Assert.Equal(0b10001, PlayerLeds.Mask(new PlayerLedConfig { Mode = PlayerLedMode.Custom, Mask = 0b110001 }));
    }
}
