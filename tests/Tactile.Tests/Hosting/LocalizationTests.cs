using Tactile.Core;

namespace Tactile.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;

internal sealed class TestLanguage : IDisposable
{
    private readonly Language _previous = Text.Current;

    public TestLanguage(Language language) => Text.Current = language;

    public void Dispose() => Text.Current = _previous;
}

[Collection("Localization")]
public sealed class LocalizationTests
{
    [Fact]
    public void Settings_store_does_not_change_the_current_language()
    {
        using var language = new TestLanguage(Language.Tr);
        using var dir = new TempDir();

        var settings = new SettingsStore(dir.Path);
        Assert.Equal(Language.En, settings.Current.Language);
        Assert.Equal(Language.En, Validation.Normalize(new AppSettings { Language = (Language)99 }).Language);
        settings.Save(settings.Current with { Language = Language.En });
        Assert.Equal(Language.Tr, Text.Current);
    }

    [Fact]
    public void App_core_sets_the_language_on_load_and_on_save()
    {
        using var language = new TestLanguage(Language.Tr);
        using var dir = new TempDir();
        using (var core = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false), new ManualTime(), runTimer: false))
        {
            Assert.Equal(Language.En, Text.Current);
            Assert.Equal("English", Text.Pick("English", "Türkçe"));

            core.Settings.Save(core.Settings.Current with { Language = Language.Tr });
            Assert.Equal("Türkçe", Text.Pick("English", "Türkçe"));
        }

        Text.Current = Language.En;
        using var reloaded = new AppCore(dir.Path, new FakeForeground(), new ControllerService(autoConnect: false), new ManualTime(), runTimer: false);
        Assert.Equal(Language.Tr, Text.Current);
    }
}
