using System.Text.Json;
using RootDock.Core.Settings;
using RootDock.Core.Sftp;
using Xunit;

namespace RootDock.Core.Tests;

public sealed class FileColumnSettingsTests
{
    [Fact]
    public void Automatic_name_fills_the_space_left_by_the_other_columns()
    {
        var columns = new FileColumnSettings();
        var others = columns.OthersWidth;

        Assert.Equal(1000 - others, columns.WidthOf(FileColumn.Name, 1000));
        Assert.Equal(1000, columns.TotalWidth(1000));
    }

    [Fact]
    public void Automatic_name_keeps_a_readable_minimum_in_a_narrow_panel()
    {
        var columns = new FileColumnSettings();

        Assert.Equal(FileColumnSettings.AutoNameMinWidth, columns.WidthOf(FileColumn.Name, 300));
        Assert.True(columns.TotalWidth(300) > 300); // o resto aparece rolando para o lado
    }

    [Fact]
    public void A_resized_name_stops_following_the_panel_until_reset()
    {
        var columns = new FileColumnSettings();
        columns.SetWidth(FileColumn.Name, 240);

        Assert.Equal(240, columns.WidthOf(FileColumn.Name, 300));
        Assert.Equal(240, columns.WidthOf(FileColumn.Name, 2000));

        columns.AutoSizeName();
        Assert.Equal(2000 - columns.OthersWidth, columns.WidthOf(FileColumn.Name, 2000));
    }

    [Theory]
    [InlineData(-50, FileColumnSettings.MinWidth)]
    [InlineData(5, FileColumnSettings.MinWidth)]
    [InlineData(99999, FileColumnSettings.MaxWidth)]
    [InlineData(double.NaN, FileColumnSettings.MinWidth)]
    [InlineData(150.4, 150)]
    public void Widths_are_clamped_and_rounded(double asked, double expected)
    {
        var columns = new FileColumnSettings();
        columns.SetWidth(FileColumn.Size, asked);

        Assert.Equal(expected, columns.WidthOf(FileColumn.Size, 0));
    }

    [Fact]
    public void Name_has_a_larger_minimum_and_can_never_be_hidden()
    {
        var columns = new FileColumnSettings();
        columns.SetWidth(FileColumn.Name, 10);
        columns.SetVisible(FileColumn.Name, false);

        Assert.Equal(FileColumnSettings.MinNameWidth, columns.WidthOf(FileColumn.Name, 0));
        Assert.True(columns.IsVisible(FileColumn.Name));
    }

    [Fact]
    public void Hidden_columns_take_no_space_and_give_it_to_the_name()
    {
        var columns = new FileColumnSettings();
        var before = columns.WidthOf(FileColumn.Name, 1000);
        var owner = columns.WidthOf(FileColumn.Owner, 1000);

        columns.SetVisible(FileColumn.Owner, false);
        columns.SetVisible(FileColumn.Owner, false); // repetir não duplica

        Assert.Equal(0, columns.WidthOf(FileColumn.Owner, 1000));
        Assert.Equal(before + owner, columns.WidthOf(FileColumn.Name, 1000));
        Assert.Single(columns.Hidden);

        columns.SetVisible(FileColumn.Owner, true);
        Assert.Equal(owner, columns.WidthOf(FileColumn.Owner, 1000));
        Assert.Empty(columns.Hidden);
    }

    [Fact]
    public void Sanitize_drops_broken_values_from_a_hand_edited_file()
    {
        var columns = new FileColumnSettings
        {
            NameWidth = -3,
            Widths = new() { ["Size"] = 120, ["Modified"] = double.PositiveInfinity, ["Owner"] = 2, ["Name"] = 400, ["Bogus"] = 90, ["size"] = 50 },
            Hidden = ["Group", "Group", "Name", "Nada"],
            SortColumn = "Inventada",
            SortDescending = true,
        };

        columns.Sanitize();

        Assert.Null(columns.NameWidth);
        Assert.Equal(["Size"], columns.Widths.Keys);
        Assert.Equal(["Group"], columns.Hidden);
        Assert.Equal("Name", columns.SortColumn);
        Assert.False(columns.SortDescending);
    }

    [Fact]
    public void Reset_brings_back_defaults()
    {
        var columns = new FileColumnSettings();
        columns.SetWidth(FileColumn.Name, 300);
        columns.SetWidth(FileColumn.Permissions, 300);
        columns.SetVisible(FileColumn.Size, false);

        columns.Reset();

        Assert.Null(columns.NameWidth);
        Assert.Equal(FileColumnSettings.DefaultWidth(FileColumn.Permissions), columns.WidthOf(FileColumn.Permissions, 0));
        Assert.True(columns.IsVisible(FileColumn.Size));
    }

    [Fact]
    public void Survives_a_round_trip_through_settings_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rootdock-cols-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings();
            settings.FilesColumns.SetWidth(FileColumn.Name, 222);
            settings.FilesColumns.SetWidth(FileColumn.Modified, 140);
            settings.FilesColumns.SetVisible(FileColumn.Group, false);
            settings.FilesColumns.SortColumn = "Modified";
            settings.FilesColumns.SortDescending = true;
            store.Save(settings);
            Assert.DoesNotContain("othersWidth", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            var loaded = store.Load().FilesColumns;

            Assert.Equal(222, loaded.NameWidth);
            Assert.Equal(140, loaded.WidthOf(FileColumn.Modified, 0));
            Assert.False(loaded.IsVisible(FileColumn.Group));
            Assert.Equal("Modified", loaded.SortColumn);
            Assert.True(loaded.SortDescending);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void Old_settings_without_columns_get_the_defaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"FontSize\": 15}")!;

        Assert.NotNull(settings.FilesColumns);
        Assert.Null(settings.FilesColumns.NameWidth);
        Assert.Empty(settings.FilesColumns.Hidden);
    }
}
