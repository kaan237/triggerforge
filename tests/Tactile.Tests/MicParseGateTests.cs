using Tactile.Device.DualSense;

namespace Tactile.Tests;

public class MicParseGateTests
{
    [Fact]
    public void Stream_on_publishes_mic_frames()
    {
        Assert.Equal(MicParseDecision.Publish, MicParseGate.Decide(true, null));
        Assert.Equal(MicParseDecision.Publish, MicParseGate.Decide(true, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Grace_period_drops_late_frames()
    {
        Assert.Equal(MicParseDecision.DropGrace, MicParseGate.Decide(false, TimeSpan.Zero));
        Assert.Equal(MicParseDecision.DropGrace, MicParseGate.Decide(false, TimeSpan.FromMilliseconds(1999)));
    }

    [Fact]
    public void After_grace_period_input_is_normal()
    {
        Assert.Equal(MicParseDecision.Normal, MicParseGate.Decide(false, TimeSpan.FromMilliseconds(2001)));
    }

    [Fact]
    public void Never_on_is_normal()
    {
        Assert.Equal(MicParseDecision.Normal, MicParseGate.Decide(false, null));
    }
}
