using RootDock.Core.Sessions;
using RootDock.Core.Settings;
using Xunit;

namespace RootDock.Core.Tests;

public sealed class ScreensaverSettingsTests
{
    [Fact]
    public void Settings_from_older_versions_get_the_screensaver_on_with_ten_minutes()
    {
        // settings.json da 0.1.8: o bloco "terminal" ainda não tem os campos da proteção de tela.
        var json = "{\"fontSize\": 14, \"terminal\": {\"fontFamily\": \"Consolas\", \"theme\": \"black\"}}";
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SessionStore.JsonOptions)!;

        Assert.Equal("Consolas", settings.Terminal.FontFamily);
        Assert.True(settings.Terminal.Screensaver);
        Assert.Equal(10, settings.Terminal.ScreensaverMinutes);
        Assert.Equal("random", settings.Terminal.ScreensaverScene);
    }

    [Fact]
    public void Screensaver_choices_survive_a_round_trip_through_settings_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rootdock-saver-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings();
            settings.Terminal.Screensaver = false;
            settings.Terminal.ScreensaverMinutes = 25;
            settings.Terminal.ScreensaverScene = "chuva";
            store.Save(settings);

            var loaded = store.Load().Terminal;

            Assert.False(loaded.Screensaver);
            Assert.Equal(25, loaded.ScreensaverMinutes);
            Assert.Equal("chuva", loaded.ScreensaverScene);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}
