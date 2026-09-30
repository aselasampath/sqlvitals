using System.IO;
using System.Text.Json.Nodes;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.Tests;

/// <summary>Chat channels in settings.dat (#120): encrypted, round-tripped, dropped alone when unreadable, logged by name.</summary>
public sealed class ChatChannelSettingsTests : IDisposable
{
    private const string Url = "https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private string SettingsFile => Path.Combine(_dir, "settings.dat");

    private ConnectionSettingsService Create() => new(_dir);

    private static ChatChannel Channel(string name = "DBA team (Slack)", string url = Url) => new()
    {
        Name       = name,
        Service    = ChatService.Slack,
        WebhookUrl = url,
    };

    [Fact]
    public void AChannel_SurvivesSaveAndLoad()
    {
        var channel = Channel();
        channel.Enabled = false;
        Create().SaveChatChannel(channel);

        var loaded = Assert.Single(Create().Load().ChatChannels);

        Assert.Equal((channel.Id, "DBA team (Slack)", ChatService.Slack, Url, false),
                     (loaded.Id, loaded.Name, loaded.Service, loaded.WebhookUrl, loaded.Enabled));
    }

    [Fact]
    public void Saving_TrimsTheNameAndUrl_AndReplacesTheChannelWithTheSameId()
    {
        var service = Create();
        var channel = Channel(name: "  Ops  ", url: $"  {Url}  ");
        service.SaveChatChannel(channel);
        channel.Name = "Ops team";
        service.SaveChatChannel(channel);

        var loaded = Assert.Single(Create().Load().ChatChannels);
        Assert.Equal(("Ops team", Url), (loaded.Name, loaded.WebhookUrl));
    }

    [Fact]
    public void TheFile_NeverHoldsTheUrlOrTheChannelInPlainText()
    {
        Create().SaveChatChannel(Channel());

        var text = File.ReadAllText(SettingsFile);

        Assert.DoesNotContain("hooks.slack.com", text);
        Assert.DoesNotContain("DBA team", text);
        Assert.NotNull(JsonNode.Parse(text)!["EncryptedChatChannels"]);
    }

    [Fact]
    public void NoChannels_WritesNoBlob()
    {
        var service = Create();
        var channel = Channel();
        service.SaveChatChannel(channel);
        service.RemoveChatChannel(channel.Id);

        Assert.Empty(Create().Load().ChatChannels);
        Assert.Null(JsonNode.Parse(File.ReadAllText(SettingsFile))!["EncryptedChatChannels"]);
    }

    [Fact]
    public void AChannelListThatCantBeDecrypted_IsDropped_AndTheRestOfTheSettingsLoad()
    {
        var service = Create();
        service.Upsert(ConnectionSettingsServiceTests.Connection());
        service.SetKeepRunningInTray(false);
        service.SaveChatChannel(Channel());
        // As if copied from another PC or Windows user: DPAPI can't decrypt it.
        ReplaceBlob(Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

        var store = Create().Load();

        Assert.Empty(store.ChatChannels);
        Assert.True(store.ChatChannelsDropped);
        Assert.Single(store.Connections);
        Assert.False(store.KeepRunningInTray);
        Assert.False(store.ReadFailed);
    }

    [Fact]
    public void AfterTheNextSave_TheDroppedChannelsAreGone_AndSoIsTheFlag()
    {
        var service = Create();
        service.SaveChatChannel(Channel());
        ReplaceBlob("not base64 at all");

        Create().SetKeepRunningInTray(false);

        var store = Create().Load();
        Assert.Empty(store.ChatChannels);
        Assert.False(store.ChatChannelsDropped);
    }

    [Fact]
    public void AddingEditingAndRemoving_AreLoggedByName_NeverWithTheUrl()
    {
        var service = Create();
        var raised  = new List<ActivityEntry>();
        service.Changed += raised.AddRange;

        var channel = Channel();
        service.SaveChatChannel(channel);
        var edited = channel.Clone();
        edited.WebhookUrl = "https://hooks.slack.com/services/T0001/B0002/NEWnewNEWnew";
        edited.Enabled    = false;
        service.SaveChatChannel(edited);
        service.RemoveChatChannel(channel.Id);

        Assert.Collection(raised,
            r => Assert.Equal((ActivityEvents.ChatChannelAdded, "DBA team (Slack)"), (r.Event, r.Detail)),
            r => Assert.Equal((ActivityEvents.ChatChannelEdited, "DBA team (Slack): Webhook URL", SettingsDiff.SecretChanged),
                              (r.Event, r.Setting, r.Detail)),
            r => Assert.Equal((ActivityEvents.ChatChannelEdited, "DBA team (Slack): On", "On", "Off"),
                              (r.Event, r.Setting, r.OldValue, r.NewValue)),
            r => Assert.Equal((ActivityEvents.ChatChannelRemoved, "DBA team (Slack)"), (r.Event, r.Detail)));
        foreach (var text in raised.SelectMany(r => new[] { r.ConnectionName, r.Setting, r.OldValue, r.NewValue, r.Detail }).OfType<string>())
            Assert.DoesNotContain("hooks.slack.com", text);
    }

    [Fact]
    public void SavingAChannelUnchanged_RaisesNoRows()
    {
        var service = Create();
        var channel = Channel();
        service.SaveChatChannel(channel);
        var raised = new List<ActivityEntry>();
        service.Changed += raised.AddRange;

        service.SaveChatChannel(channel);

        Assert.Empty(raised);
    }

    private void ReplaceBlob(string value)
    {
        var json = JsonNode.Parse(File.ReadAllText(SettingsFile))!.AsObject();
        json["EncryptedChatChannels"] = value;
        File.WriteAllText(SettingsFile, json.ToJsonString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
