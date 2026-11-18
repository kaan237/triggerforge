using System.Text.Json;
using Tactile.Core;

namespace Tactile.Tests;

public class LenientJsonTests
{
    [Fact]
    public void Unknown_enum_value_reads_as_default_instead_of_failing()
    {
        const string json = """{ "version": 1, "name": "x", "lightbar": { "effect": "pulse", "color": "#102030" }, "micLed": "on" }""";
        Profile p = JsonSerializer.Deserialize<Profile>(json, TactileJson.Options)!;

        Assert.Equal(default(LightbarEffect), p.Lightbar.Effect);
        Assert.Equal("#102030", p.Lightbar.Color);
        Assert.Equal(Tactile.Device.DualSense.MicLed.On, p.MicLed);
    }

    [Fact]
    public void Enums_are_written_as_camel_case_strings()
    {
        string json = JsonSerializer.Serialize(new Profile { Name = "x" }, TactileJson.Compact);
        Assert.Contains("\"effect\":\"static\"", json);
        Assert.Contains("\"brightness\":\"high\"", json);
    }

    [Fact]
    public void Turkish_text_is_not_escaped_in_files()
    {
        string json = JsonSerializer.Serialize(new Profile { Name = "Varsayılan" }, TactileJson.Options);
        Assert.Contains("Varsayılan", json);
    }
}
