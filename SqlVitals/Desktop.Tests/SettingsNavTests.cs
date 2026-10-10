using SqlVitals.Desktop.Services;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>Settings' side menu (#131), where each setting lives, and links into Settings.</summary>
public sealed class SettingsNavTests
{
    // A store with one of everything, so every connection and channel field is in the snapshot.
    private static ConnectionStore Store()
    {
        var conn = new ConnectionSettings
        {
            Server           = "uat-sql01",
            Name             = "UAT",
            HealthThresholds = HealthThresholds.Default,
        };
        return new ConnectionStore
        {
            Connections  = [conn],
            ChatChannels = [new ChatChannel { Name = "DBA team", WebhookUrl = "https://hooks.slack.com/services/T/B/x" }],
        };
    }

    [Fact]
    public void EverySettingTheActivityLogNames_IsPlaced_InACategoryOrOnAPage()
    {
        var snapshot = SettingsActivity.Snapshot(Store());

        var unplaced = snapshot.Settings.Select(s => s.Name).Where(n => SettingsNav.PlaceOf(n) is null).ToList();

        Assert.True(unplaced.Count == 0,
            $"Place these in SettingsNav.AppSettings (a Settings category, or the page they're on): {string.Join(", ", unplaced)}");
    }

    [Fact]
    public void EveryConnectionSetting_IsOnATabOfItsPage()
    {
        var snapshot = SettingsActivity.Snapshot(Store());

        var unplaced = snapshot.Connections.SelectMany(c => c.Fields).Select(f => f.Name)
                               .Where(n => SettingsNav.TabOf(n) is null).Distinct().ToList();

        Assert.True(unplaced.Count == 0, $"Place these in SettingsNav.ConnectionSettings: {string.Join(", ", unplaced)}");
    }

    [Fact]
    public void EveryPlacedSetting_StillExists_SoTheListCantGoStale()
    {
        var snapshot = SettingsActivity.Snapshot(Store());
        var names    = snapshot.Settings.Select(s => s.Name.Split(':')[0]).Append("Theme").ToHashSet();
        var fields   = snapshot.Connections.SelectMany(c => c.Fields).Select(f => f.Name.Split(':')[0]).ToHashSet();

        Assert.DoesNotContain(SettingsNav.AppSettings.Keys, k => !names.Contains(k));
        Assert.DoesNotContain(SettingsNav.ConnectionSettings.Keys, k => !fields.Contains(k));
    }

    [Fact]
    public void TheCategoriesOfTheIssue_AreInFourGroups_EachOnce()
    {
        Assert.Equal(["Servers", "Monitoring", "Notifications", "App"], SettingsNav.Groups.Select(g => g.Name));
        Assert.Equal(
            ["Connections", "Monitoring & history", "Health thresholds", "Collector service", "Alerts & chat", "Notification area",
             "Appearance", "Activity log & diagnostics"],
            SettingsNav.Categories.Select(c => c.Title));
        Assert.Equal(Enum.GetValues<SettingsCategory>(), SettingsNav.Categories.Select(c => c.Id).Order());
    }

    [Fact]
    public void CategoriesForTheWholeApp_SayForEveryConnection()
    {
        foreach (var info in SettingsNav.Categories.Where(c => c.Id is not (SettingsCategory.Connections or SettingsCategory.Thresholds)))
            Assert.StartsWith("For every connection", info.Subtitle);
        Assert.DoesNotContain(SettingsNav.Categories, c => c.Subtitle.Contains("below", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SettingsInTheSamePlaceAsBefore_StayTogether()
    {
        Assert.Equal(SettingPlace.In(SettingsCategory.Monitoring), SettingsNav.PlaceOf("Command timeout"));
        Assert.Equal(SettingPlace.In(SettingsCategory.Thresholds), SettingsNav.PlaceOf("Health threshold: CPU"));
        Assert.Equal(SettingPlace.OnPage("Backups"), SettingsNav.PlaceOf("Backup RPO (full)"));
        Assert.Equal(ConnectionTab.Thresholds, SettingsNav.TabOf("Own health threshold: Blocking"));
        Assert.Equal(ConnectionTab.Monitoring, SettingsNav.TabOf("Windows notifications"));
        Assert.Equal(ConnectionTab.Advanced, SettingsNav.TabOf("Additional parameters"));
    }

    [Fact]
    public void ALocation_IsWrittenAsAPath_AndReadBack()
    {
        var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
        SettingsLocation[] locations =
        [
            new(SettingsCategory.Alerts),
            SettingsLocation.Connection(id, ConnectionTab.Thresholds),
            SettingsLocation.Connection(id),
            SettingsLocation.NewConnectionPage,
        ];

        Assert.Equal("Settings/Connections/6f9619ff-8b86-d011-b42d-00cf4fc964ff/Thresholds", locations[1].ToString());
        Assert.Equal("Settings/Alerts", locations[0].ToString());
        foreach (var location in locations)
            Assert.Equal(location, SettingsLocation.Parse(location.ToString()));
        Assert.Equal(SettingsLocation.Connection(id), SettingsLocation.Parse($"settings/connections/{id}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Settings")]
    [InlineData("Home/Connections")]
    [InlineData("Settings/Nope")]
    [InlineData("Settings/42")]
    [InlineData("Settings/Alerts/6f9619ff-8b86-d011-b42d-00cf4fc964ff")]
    [InlineData("Settings/Connections/not-a-guid")]
    [InlineData("Settings/Connections/6f9619ff-8b86-d011-b42d-00cf4fc964ff/Nope")]
    public void ANonsenseLocation_IsNull(string? text) => Assert.Null(SettingsLocation.Parse(text));
}
