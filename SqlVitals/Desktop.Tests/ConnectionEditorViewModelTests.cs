using System.IO;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Settings;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The connection editor's view model on its own (#190): no page, no UI thread, and Test
/// connection and the database list as fakes, so no server is contacted. Its rules, Save and
/// Cancel, what's unsaved, and leaving with it.
/// </summary>
public sealed class ConnectionEditorViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConnectionSettingsService _settings;
    private readonly MonitoringManager _monitoring;
    private readonly ChatNotificationService _chat;
    private readonly FakeDialogService _dialogs = new();
    private readonly List<string> _signedIn = [];
    private readonly List<string> _listed = [];
    private readonly List<bool> _applied = [];

    // What the fake sign-in answers: null signs in, anything else is why not.
    private string? _signIn;

    // What the fake database list answers, or throws.
    private Func<IReadOnlyList<string>> _databases = () => ["master", "Sales"];

    public ConnectionEditorViewModelTests()
    {
        _settings   = new ConnectionSettingsService(_dir);
        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }), (_, _) => new FakeRepository());
        _chat       = new ChatNotificationService(new NoSender(), "DBA-PC01", _ => { });
    }

    private SettingsContext Context() => new(_settings, _monitoring, _chat, Apply, _dialogs)
    {
        Ui             = new FakeUiDispatcher(),
        Theme          = new FakeThemeService(),
        TestConnection = connectionString =>
        {
            _signedIn.Add(connectionString);
            return Task.FromResult(_signIn);
        },
        ListDatabases  = connectionString =>
        {
            _listed.Add(connectionString);
            return Task.FromResult(_databases());
        },
    };

    private Task Apply(bool connect)
    {
        _applied.Add(connect);
        return Task.CompletedTask;
    }

    private ConnectionEditorViewModel New() => new(Context(), connectionId: null);

    private ConnectionSettings Saved(string name = "Prod", string server = "sql01")
    {
        var conn = new ConnectionSettings { Name = name, Server = server, Database = "Sales", Authentication = SqlAuthMode.Windows };
        _settings.Upsert(conn);
        return conn;
    }

    // A new connection with everything a SQL login needs.
    private ConnectionEditorViewModel Filled()
    {
        var editor = New();
        editor.Server   = "sql01";
        editor.UserName = "app";
        editor.Password = "s3cret";
        return editor;
    }

    // ── The rules ─────────────────────────────────────────────────────────

    [Fact]
    public void ANewConnection_CantBeSaved_UntilItHasAServerAndALogin_AndSaysWhy_WithoutMarkingWhatIsntTypedYet()
    {
        var editor = New();

        Assert.True(editor.HasErrors);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal("Server name is required.", editor.Problem);
        Assert.Null(editor.ServerError);
        Assert.Null(editor.UserNameError);

        editor.Server = "sql01";
        Assert.Equal("Login is required for SQL Server Authentication.", editor.Problem);
        Assert.Null(editor.ServerError);

        editor.UserName = "app";
        editor.Password = "s3cret";
        Assert.False(editor.HasErrors);
        Assert.Null(editor.Problem);
        Assert.True(editor.SaveCommand.CanExecute(null));

        // Typed, then cleared: now the field says it too.
        editor.Server = "  ";
        Assert.Equal("Server name is required.", editor.ServerError);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.SaveAndConnectCommand.CanExecute(null));
    }

    [Fact]
    public void WindowsAndEntra_NeedNoPassword_AndWindowsNoLogin()
    {
        var editor = New();
        editor.Server = "sql01";
        editor.Authentication = SqlAuthMode.Windows;
        Assert.False(editor.HasErrors);
        Assert.False(editor.ShowsUserName);
        Assert.False(editor.ShowsPassword);

        editor.Authentication = SqlAuthMode.EntraMfa;
        Assert.False(editor.HasErrors);
        Assert.Equal(("User name", ""), (editor.UserNameLabel, editor.UserNameRequiredMark));
        Assert.True(editor.ShowsUserNameHint);

        editor.Authentication = SqlAuthMode.SqlServer;
        Assert.Equal("Login is required for SQL Server Authentication.", editor.Problem);
        Assert.Equal(" *", editor.UserNameRequiredMark);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public void TheConnectionTimeout_IsAPositiveWholeNumber(string typed)
    {
        var editor = Filled();

        editor.ConnectTimeout = typed;

        Assert.Equal("Connection timeout (Advanced) must be a positive integer.", editor.ConnectTimeoutError);
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void AdditionalParameters_MayNotHoldCredentials_NorBeUnreadable_AndTheErrorNeverEchoesThem()
    {
        var editor = Filled();

        editor.AdditionalParameters = "Encrypt=True;Password=hunter2";
        Assert.StartsWith("Additional parameters (Advanced) must not contain a user name or password.", editor.AdditionalParametersError);

        editor.AdditionalParameters = "NoSuchKeyword=1";
        Assert.StartsWith("Additional parameters (Advanced) are not valid:", editor.AdditionalParametersError);

        editor.AdditionalParameters = "MultipleActiveResultSets=True";
        Assert.Null(editor.AdditionalParametersError);
        Assert.DoesNotContain("hunter2", string.Join("|", editor.GetErrors().Select(e => e.ErrorMessage)));
    }

    [Fact]
    public async Task ANameAnotherConnectionHas_CantBeSaved_EvenWhenItIsTheServerAndDatabase()
    {
        Saved("Prod");
        var editor = Filled();

        editor.Name = "prod";
        Assert.Equal("A connection named \"prod\" already exists. Enter a different connection name.", editor.NameError);
        var focused = new List<string>();
        editor.FocusRequested += focused.Add;
        editor.SelectTab(ConnectionTab.Advanced);
        Assert.False(await editor.SaveAsync(connect: false));
        Assert.Equal(ConnectionTab.Connection, editor.SelectedTab);
        Assert.Contains(nameof(ConnectionEditorViewModel.Name), focused);

        // Blank shows as "server / database": the same as another unnamed one is taken too.
        _settings.Upsert(new ConnectionSettings { Server = "sql02", Database = "Sales", Authentication = SqlAuthMode.Windows });
        var unnamed = new ConnectionEditorViewModel(Context(), null) { Authentication = SqlAuthMode.Windows, Server = "sql02", Database = "Sales" };
        Assert.Equal("A connection named \"sql02 / Sales\" already exists. Enter a different connection name.", unnamed.Problem);
        unnamed.Database = "HR";
        Assert.Null(unnamed.Problem);
    }

    [Fact]
    public async Task ItsOwnThresholds_MustBeReadable_AndSavingSaysWhichOnTheirTab()
    {
        var conn   = Saved();
        var editor = new ConnectionEditorViewModel(Context(), conn.Id) { OwnThresholds = true };
        var focused = new List<ThresholdField>();
        editor.Thresholds.FocusRequested += focused.Add;
        var cpu = editor.Thresholds.Row(HealthIndicator.Cpu).Warning;

        cpu.Text = "abc";

        Assert.StartsWith("Health thresholds — CPU", editor.ThresholdsError);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Empty(focused);
        Assert.False(await editor.SaveAsync(connect: false));
        Assert.Equal(ConnectionTab.Thresholds, editor.SelectedTab);
        // The keyboard goes to the box to put right, as the save before #190 did.
        Assert.Equal([cpu], focused);

        // Off: the defaults again, nothing to read, and nothing unsaved; Discard tells the error by its bound name.
        var told = new List<string?>();
        editor.PropertyChanged += (_, e) => told.Add(e.PropertyName);
        editor.OwnThresholds = false;
        Assert.Null(editor.ThresholdsError);
        Assert.False(editor.IsDirty);
        editor.Discard();
        Assert.Contains(nameof(ConnectionEditorViewModel.ThresholdsError), told);
        Assert.DoesNotContain("OwnThresholdsError", told);
    }

    // ── Save and Cancel ───────────────────────────────────────────────────

    [Fact]
    public async Task Save_SignsInFirst_AndSavesNothingWhenItCant_SayingWhyWithoutTheSecret()
    {
        var editor = Filled();
        _signIn = "Login failed for user 'app'. Password=hunter2;";

        Assert.False(await editor.SaveAsync(connect: false));

        Assert.Single(_signedIn);
        Assert.Empty(_settings.Load().Connections);
        Assert.StartsWith("✗  Connection failed — not saved: Login failed for user 'app'.", editor.Status.Text);
        Assert.DoesNotContain("hunter2", editor.Status.Text);
        Assert.True(editor.IsNew);
    }

    [Fact]
    public async Task Save_OfANewConnection_SignsIn_SavesIt_AndTheFirstIsActive()
    {
        var editor = Filled();
        editor.Name = "Prod";
        var saved = new List<Guid>();
        editor.ConnectionSaved += saved.Add;

        Assert.True(await editor.SaveAsync(connect: false));

        var conn = Assert.Single(_settings.Load().Connections);
        Assert.Equal(("Prod", "sql01", "app"), (conn.Name, conn.Server, conn.UserName));
        Assert.Equal(conn.Id, _settings.Load().ActiveConnectionId);
        Assert.Single(_signedIn);
        Assert.Equal([false], _applied);
        Assert.Equal([conn.Id], saved);
        Assert.Equal(conn.Id, editor.ConnectionId);
        Assert.Equal("Prod", editor.Title);
        Assert.False(editor.IsDirty);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal("✓  Saved \"Prod\", and it's now the active connection.", editor.Status.Text);
    }

    [Fact]
    public async Task Save_OfWhatDoesntChangeHowItConnects_DoesntSignIn()
    {
        var conn   = Saved();
        var editor = new ConnectionEditorViewModel(Context(), conn.Id) { Name = "Prod 2", Notifies = false };

        Assert.True(await editor.SaveAsync(connect: false));

        Assert.Empty(_signedIn);
        Assert.Equal("Prod 2", _settings.Load().Connections.Single().Name);
        Assert.False(_settings.Load().NotifiesFor(conn.Id));
    }

    [Fact]
    public void WhatsUnsaved_IsToldByTab_PuttingAValueBackLeavesNothing_AndDiscardPutsBackWhatsSaved()
    {
        var conn   = Saved();
        var editor = new ConnectionEditorViewModel(Context(), conn.Id);
        Assert.False(editor.IsDirty);
        Assert.False(editor.DiscardCommand.CanExecute(null));

        editor.Name = "Other";
        editor.Name = "Prod";
        Assert.False(editor.IsDirty);

        editor.Name = "Other";
        editor.MonitorInBackground = true;
        editor.ConnectTimeout = "45";
        Assert.True(editor.IsDirty);
        Assert.Equal("●  Unsaved changes on Connection, Monitoring & alerts and Advanced", editor.UnsavedText);
        Assert.True(editor.DiscardCommand.CanExecute(null));

        editor.DiscardCommand.Execute(null);

        Assert.False(editor.IsDirty);
        Assert.Equal(("Prod", false, "30"), (editor.Name, editor.MonitorInBackground, editor.ConnectTimeout));
        Assert.Equal(string.Empty, editor.UnsavedText);
    }

    [Fact]
    public async Task Leaving_WithUnsavedChanges_AsksToSave_CancelStays_NoDiscards_YesSaves()
    {
        var conn   = Saved();
        var editor = new ConnectionEditorViewModel(Context(), conn.Id) { Name = "Prod 2" };

        _dialogs.SaveAnswer = DialogAnswer.Cancel;
        Assert.False(await editor.TryCloseAsync());
        Assert.True(editor.IsDirty);
        Assert.StartsWith("AskToSave: Unsaved changes: Save the changes to \"Prod\"?", _dialogs.Shown.Single(), StringComparison.Ordinal);

        _dialogs.SaveAnswer = DialogAnswer.No;
        Assert.True(await editor.TryCloseAsync());
        Assert.Equal("Prod", editor.Name);

        editor.Name = "Prod 3";
        _dialogs.SaveAnswer = DialogAnswer.Yes;
        Assert.True(await editor.TryCloseAsync());
        Assert.Equal("Prod 3", _settings.Load().Connections.Single().Name);

        // Nothing unsaved: it doesn't ask.
        Assert.True(await editor.TryCloseAsync());
        Assert.Equal(3, _dialogs.Shown.Count);
    }

    [Fact]
    public async Task Back_LeavesOnlyWhenItMay()
    {
        var conn   = Saved();
        var editor = new ConnectionEditorViewModel(Context(), conn.Id) { Name = "Prod 2" };
        var closed = 0;
        editor.Closed += () => closed++;

        await editor.BackCommand.ExecuteAsync(null);
        Assert.Equal(0, closed);

        _dialogs.SaveAnswer = DialogAnswer.No;
        await editor.BackCommand.ExecuteAsync(null);
        Assert.Equal(1, closed);
    }

    // ── Test connection ───────────────────────────────────────────────────

    [Fact]
    public async Task TestConnection_SignsInWithWhatsTyped_SavedOrNot_AndSaysHowItWent()
    {
        var editor = Filled();

        await editor.TestCommand.ExecuteAsync(null);
        Assert.Equal("✓  Connection successful!", editor.Status.Text);
        Assert.Contains("sql01", _signedIn.Single(), StringComparison.Ordinal);
        Assert.Empty(_settings.Load().Connections);

        _signIn = "A network-related error occurred. Password=hunter2;";
        await editor.TestCommand.ExecuteAsync(null);
        Assert.StartsWith("✗  Connection failed: A network-related error occurred.", editor.Status.Text);
        Assert.DoesNotContain("hunter2", editor.Status.Text);
    }

    [Fact]
    public async Task TestConnection_WithAFieldMissing_SaysWhichAndDoesntSignIn()
    {
        var editor = New();
        editor.Server = "sql01";

        await editor.TestCommand.ExecuteAsync(null);

        Assert.Empty(_signedIn);
        Assert.Equal("✗  Login is required for SQL Server Authentication.", editor.Status.Text);
    }

    // ── The database list ─────────────────────────────────────────────────

    [Fact]
    public async Task OpeningTheDatabaseList_ListsTheServers_KeepsWhatsTyped_AndAsksOncePerServer()
    {
        var editor = Filled();
        editor.Database = "Sales";

        editor.IsDatabaseListOpen = true;
        await editor.ListDatabasesAsync();

        Assert.Equal(["master", "Sales"], editor.Databases);
        Assert.Equal("Sales", editor.Database);
        Assert.Equal("2 database(s) on sql01.", editor.DatabaseHint);
        Assert.Single(_listed);
        Assert.DoesNotContain("Initial Catalog=Sales", _listed[0], StringComparison.OrdinalIgnoreCase);

        editor.IsDatabaseListOpen = false;
        editor.IsDatabaseListOpen = true;
        Assert.Single(_listed);
    }

    [Fact]
    public void ADatabaseListThatCantBeRead_ClosesTheList_AndSaysWhyWithoutTheSecret()
    {
        var editor = Filled();
        _databases = () => throw new InvalidOperationException("Login failed. Password=hunter2;");

        editor.IsDatabaseListOpen = true;

        Assert.False(editor.IsDatabaseListOpen);
        Assert.StartsWith("Could not list databases: Login failed.", editor.DatabaseHint);
        Assert.DoesNotContain("hunter2", editor.DatabaseHint);
    }

    // ── Secrets ───────────────────────────────────────────────────────────

    [Fact]
    public void ThePassword_IsInNoErrorAndNoDraftsText()
    {
        var editor = Filled();
        editor.Password = "hunter2";
        editor.ConnectTimeout = "x";

        Assert.DoesNotContain("hunter2", string.Join("|", editor.GetErrors().Select(e => e.ErrorMessage)));
        var draft = new ConnectionDraft("n", "s", SqlAuthMode.SqlServer, "u", "hunter2", true, "", EncryptMode.Mandatory, false, "30", "",
                                        false, true, false, "");
        Assert.DoesNotContain("hunter2", draft.ToString());
    }

    private sealed class NoSender : IWebhookSender
    {
        public Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default) =>
            throw new InvalidOperationException("No chat in these tests.");

        public Task<WebhookResult> PingAsync(Uri url, CancellationToken cancel = default) =>
            throw new InvalidOperationException("No chat in these tests.");
    }

    public void Dispose()
    {
        _monitoring.Dispose();
        _chat.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
