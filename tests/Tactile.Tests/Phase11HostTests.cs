using System.Text.Json;
using Tactile.Core;
using Tactile.Core.Updates;
using Tactile.Host;

namespace Tactile.Tests;

[Collection("Localization")]
public class Phase11HostTests
{
    [Fact]
    public void Test_turn_requires_a_ready_application_source_and_clamps_the_payload()
    {
        using var dir = new TempDir();
        var time = new ManualTime();
        var platform = new FakePlatform();
        using var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false),
            time, runTimer: false, platform.Platform);
        var paths = new AppPaths(dir.Path, dir.Path, dir.Path, dir.Path);
        using var updates = new UpdateService(paths, new Version(1, 2, 0));
        var messages = new List<string>();
        var bridge = new Bridge(core, null!, messages.Add, paths, updates);
        core.Notice += bridge.PostNotice;
        core.OnInput(Inputs.Make());

        void Tick(int milliseconds)
        {
            time.Advance(TimeSpan.FromMilliseconds(milliseconds));
            core.Refresh();
        }

        const string turn = """{"type":"mapping.testTurn","counts":100}""";
        bridge.Handle(turn, appSource: true);
        Tick(2200);
        bridge.Handle("""{"type":"hello"}""", appSource: true);
        bridge.Handle(turn, appSource: false);
        Tick(2200);
        Assert.Equal(0, platform.Injector.MovedX);

        messages.Clear();
        bridge.Handle(turn, appSource: true);
        Tick(1999);
        Assert.Equal(0, platform.Injector.MovedX);
        Tick(101);
        Assert.Equal(250, platform.Injector.MovedX);
        Tick(100);
        Assert.Equal(500, platform.Injector.MovedX);

        bridge.Handle("""{"type":"mapping.testTurn","counts":40000,"delayMs":-10}""", appSource: true);
        Tick(200);
        Assert.Equal(30500, platform.Injector.MovedX);
        Assert.Equal(0, platform.Injector.MovedY);

        string[] notices = messages.Where(raw =>
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            return doc.RootElement.GetProperty("type").GetString() == "notice" &&
                doc.RootElement.GetProperty("kind").GetString() == NoticeKinds.Info;
        }).ToArray();
        Assert.Equal(2, notices.Length);
        Assert.Contains("500", notices[0]);
        Assert.Contains("30000", notices[1]);
    }
}
