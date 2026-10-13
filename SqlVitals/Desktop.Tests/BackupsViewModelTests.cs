using System.IO;
using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Backups' view model on its own (#184): no page, no STA thread, a fake repository and a settings
/// file in a temp folder. Besides the lifecycle, it owns the filter, the RPO boxes and the
/// selected database's history, which a later selection or leaving the page cancels.
/// </summary>
public sealed class BackupsViewModelTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 30, 0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeShellStatus _shell = new();
    private readonly List<BackupRpo> _readWith = [];

    private ConnectionSettingsService Settings() => new(Directory.CreateDirectory(_dir).FullName);

    private static BackupInfo Ago(TimeSpan age) => new(Now - age - TimeSpan.FromMinutes(5), Now - age, 1_048_576, false, @"B:\backups\x.bak");

    // Sales: log overdue. HR: SIMPLE and fine. Archive: never backed up.
    private static readonly DatabaseBackupRow[] Rows =
    [
        new("Sales", "FULL", "ONLINE", false, new DateTime(2024, 1, 1), false, true, true, Ago(TimeSpan.FromDays(1)), null, Ago(TimeSpan.FromHours(3))),
        new("HR", "SIMPLE", "ONLINE", false, new DateTime(2024, 1, 1), false, true, true, Ago(TimeSpan.FromDays(1)), null, null),
        new("Archive", "SIMPLE", "ONLINE", false, new DateTime(2024, 1, 1), false, true, true, null, null, null),
    ];

    private static BackupHistoryEntry Entry(string type, TimeSpan ago, bool beforeCreate = false) =>
        new(type, Now - ago - TimeSpan.FromMinutes(5), Now - ago, 1_048_576, null, false, true, false, "FULL", "sa", @"B:\backups\x.bak", beforeCreate);

    private FakeRepository Repo(Func<string, Task<IReadOnlyList<BackupHistoryEntry>>>? history = null) => new()
    {
        GetBackupStatusAsync = rpo =>
        {
            _readWith.Add(rpo);
            return Task.FromResult(BackupList.From(Rows, Now, rpo));
        },
        GetBackupHistoryAsync = (name, _) => history?.Invoke(name) ?? Task.FromResult<IReadOnlyList<BackupHistoryEntry>>([Entry("Full", TimeSpan.FromDays(1))]),
    };

    private BackupsViewModel Connected(FakeRepository repo, ConnectionSettingsService? settings = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var model = new BackupsViewModel(repo, settings ?? Settings(), time);
        model.Connect("Backups", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    [Fact]
    public async Task Loaded_ListsTheDatabases_ToActOnFirst_AndShowsTheFirstsHistory()
    {
        var model = Connected(Repo());

        await model.OnNavigatedToAsync();
        await model.HistoryRead;

        Assert.Equal(["Read Load"], _shell.Reports);
        Assert.Equal("Archive", model.Databases[0].Name);
        Assert.Equal("3 databases", model.CountText);
        Assert.Equal("Archive", model.SelectedDatabase!.Name);
        Assert.True(model.HasSelection);
        Assert.StartsWith("Archive · ", model.DetailHeader);
        Assert.Single(model.History);
        Assert.Equal("1 backup msdb keeps, newest first", model.HistoryLabel);
        Assert.Equal("3 databases  ·  1 without a full backup  ·  0 full overdue  ·  1 log backup missing or overdue", model.FoldedSummary);
        Assert.Equal([BackupsViewModel.SummaryTone.Bold, BackupsViewModel.SummaryTone.Danger, BackupsViewModel.SummaryTone.Plain, BackupsViewModel.SummaryTone.Warning],
                     model.Summary.Where(p => p.Tone != BackupsViewModel.SummaryTone.Muted).Select(p => p.Tone));
        Assert.True(model.ShowsSummary);
        Assert.StartsWith("Read at 09:30:00. Checked for ", model.ReadNote);
        Assert.False(model.ShowsNoData);
        Assert.Equal(VerdictLevel.Critical, model.Verdict!.Level);
    }

    [Fact]
    public async Task Empty_SaysWhy_AndSelectsNothing()
    {
        var model = Connected(new FakeRepository
        {
            GetBackupStatusAsync = rpo => Task.FromResult(BackupList.Unavailable("Azure SQL Database takes its own backups.", rpo)),
        });

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Databases);
        Assert.True(model.ShowsNoData);
        Assert.Equal("Azure SQL Database takes its own backups.", model.NoDataText);
        Assert.Equal("Azure SQL Database takes its own backups.", model.FoldedSummary);
        Assert.False(model.ShowsSummary);
        Assert.Null(model.Problem);
        Assert.Equal("Databases", model.CountText);
        Assert.False(model.HasSelection);
        Assert.Equal("Select a database above to see its backups.", model.DetailHeader);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var model = Connected(new FakeRepository
        {
            GetBackupStatusAsync = _ => Task.FromException<BackupList>(new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], _shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the backup history", "Error: Timeout expired."), model.Verdict);
        Assert.Equal("Reading the backup history from msdb…", model.ReadNote);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<BackupList>();
        var repo    = new FakeRepository { GetBackupStatusAsync = _ => reading.Task };
        var model   = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetBackupStatusAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(_shell.Reports);
        Assert.Null(model.Verdict);
    }

    [Fact]
    public async Task Leaving_CancelsTheHistoryRead_Too()
    {
        var history = new TaskCompletionSource<IReadOnlyList<BackupHistoryEntry>>();
        var repo    = Repo(_ => history.Task);
        var model   = Connected(repo);
        await model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetBackupHistoryAsync)];

        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        history.SetCanceled(token);
        await model.HistoryRead;
        Assert.Equal("Reading the backups msdb keeps…", model.HistoryLabel);
    }

    [Fact]
    public async Task TheDatabaseSelected_IsKeptAcrossARefresh_ByItsName()
    {
        var model = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedDatabase = model.Databases.Single(d => d.Name == "HR");
        await model.HistoryRead;
        var rows = model.Databases;

        await model.RefreshAsync();
        await model.HistoryRead;

        Assert.Same(rows, model.Databases);
        Assert.Equal("HR", model.SelectedDatabase!.Name);
        Assert.Contains(model.SelectedDatabase, model.Databases);
        Assert.StartsWith("HR · ", model.DetailHeader);
    }

    [Fact]
    public async Task AnotherDatabaseSelected_CancelsTheHistoryOfTheFirst_SoItNeverLandsOnTheSecond()
    {
        var archive = new TaskCompletionSource<IReadOnlyList<BackupHistoryEntry>>();
        var repo    = Repo(name => name == "Archive" ? archive.Task : Task.FromResult<IReadOnlyList<BackupHistoryEntry>>([Entry("Full", TimeSpan.FromDays(1)), Entry("Full", TimeSpan.FromDays(2))]));
        var model   = Connected(repo);
        await model.OnNavigatedToAsync();
        var first   = model.HistoryRead;
        var token   = repo.Tokens[nameof(FakeRepository.GetBackupHistoryAsync)];

        model.SelectedDatabase = model.Databases.Single(d => d.Name == "HR");
        await model.HistoryRead;
        archive.SetResult([]);
        await first;

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(2, model.History.Count);
        Assert.Equal("2 backups msdb keeps, newest first", model.HistoryLabel);
    }

    [Fact]
    public async Task AHistoryThatCantBeRead_IsSaidAboveIt_AndThePageStillReads()
    {
        var model = Connected(Repo(_ => Task.FromException<IReadOnlyList<BackupHistoryEntry>>(new InvalidOperationException("Timeout expired."))));

        await model.OnNavigatedToAsync();
        await model.HistoryRead;

        Assert.Equal(["Read Load"], _shell.Reports);
        Assert.Empty(model.History);
        Assert.Equal("Its backups could not be read: Error: Timeout expired.", model.HistoryLabel);
    }

    [Fact]
    public async Task TheFilter_FiltersWhatWasRead_WithoutReadingAgain()
    {
        var model = Connected(Repo());
        await model.OnNavigatedToAsync();

        model.FilterIndex = 3;   // SIMPLE recovery

        Assert.Single(_readWith);
        Assert.Equal(["Archive", "HR"], model.Databases.Select(d => d.Name).Order());
        Assert.Equal("2 of 3 databases", model.CountText);

        model.FilterIndex = 4;   // Not online
        Assert.Empty(model.Databases);
        Assert.True(model.ShowsNoData);
        Assert.Equal("Every database is online.", model.NoDataText);
        Assert.False(model.HasSelection);
    }

    [Fact]
    public async Task ShowOnlyThese_PicksTheDatabasesThatNeedAttention()
    {
        var model = Connected(Repo());
        await model.OnNavigatedToAsync();

        await model.ActCommand.ExecuteAsync(model.Verdict!.Actions.Single(a => a.Key == BackupsVerdict.ShowThem));

        Assert.Equal(BackupsViewModel.NeedsAttentionFilter, model.FilterIndex);
        Assert.Equal(["Archive", "Sales"], model.Databases.Select(d => d.Name).Order());
    }

    [Fact]
    public async Task Apply_ReadsAgainWithTheAgesTyped_AndSavesThem()
    {
        var settings = Settings();
        var model    = Connected(Repo(), settings);
        await model.OnNavigatedToAsync();

        model.FullAgeText = "2";
        model.LogAgeText  = "4h";
        await model.ApplyCommand.ExecuteAsync(null);

        var rpo = new BackupRpo(TimeSpan.FromDays(2), TimeSpan.FromHours(4));
        Assert.Equal([BackupRpo.Default, rpo], _readWith);
        Assert.Equal(rpo, new ConnectionSettingsService(_dir).Load().BackupRpo);
        Assert.Equal(BackupRpo.Format(TimeSpan.FromDays(2)), model.FullAgeText);
        Assert.Null(model.RpoError);
    }

    // As Enter in a box did before #184: pressed while it reads, the ages typed are read next.
    [Fact]
    public async Task Apply_WhileItReads_ReadsAgainAfter_WithTheAgesTypedByThen()
    {
        var first = new TaskCompletionSource<BackupList>();
        var reads = new List<BackupRpo>();
        var model = Connected(new FakeRepository
        {
            GetBackupStatusAsync = rpo =>
            {
                reads.Add(rpo);
                return reads.Count == 1 ? first.Task : Task.FromResult(BackupList.From(Rows, Now, rpo));
            },
            GetBackupHistoryAsync = (_, _) => Task.FromResult<IReadOnlyList<BackupHistoryEntry>>([]),
        });
        var shown = model.OnNavigatedToAsync();
        Assert.True(model.IsBusy);

        model.FullAgeText = "3";
        Assert.True(model.ApplyCommand.CanExecute(null));
        var applied = model.ApplyCommand.ExecuteAsync(null);
        model.LogAgeText = "2h";
        Assert.True(model.ApplyCommand.CanExecute(null));
        first.SetResult(BackupList.From(Rows, Now, BackupRpo.Default));
        await shown;
        await applied;

        Assert.Equal([BackupRpo.Default, new BackupRpo(TimeSpan.FromDays(3), TimeSpan.FromHours(2))], reads);
    }

    [Fact]
    public async Task AnotherDatabaseSelected_DoesntShowTheFirstsBackups_WhileItsOwnAreRead()
    {
        var hr    = new TaskCompletionSource<IReadOnlyList<BackupHistoryEntry>>();
        var model = Connected(Repo(name => name == "HR" ? hr.Task : Task.FromResult<IReadOnlyList<BackupHistoryEntry>>([Entry("Full", TimeSpan.FromDays(1))])));
        await model.OnNavigatedToAsync();
        await model.HistoryRead;
        Assert.Single(model.History);

        model.SelectedDatabase = model.Databases.Single(d => d.Name == "HR");

        Assert.Empty(model.History);
        Assert.Equal("Reading the backups msdb keeps…", model.HistoryLabel);
        hr.SetResult([]);
        await model.HistoryRead;
        Assert.Equal("msdb keeps no backups of this database", model.HistoryLabel);
    }

    [Fact]
    public async Task ARefresh_KeepsTheSameDatabasesBackups_WhileItReadsThemAgain()
    {
        var reads = 0;
        var again = new TaskCompletionSource<IReadOnlyList<BackupHistoryEntry>>();
        var model = Connected(Repo(_ => ++reads == 1 ? Task.FromResult<IReadOnlyList<BackupHistoryEntry>>([Entry("Full", TimeSpan.FromDays(1))]) : again.Task));
        await model.OnNavigatedToAsync();
        await model.HistoryRead;

        await model.RefreshAsync();

        Assert.Single(model.History);
        Assert.Equal("1 backup msdb keeps, newest first", model.HistoryLabel);
        again.SetResult([]);
        await model.HistoryRead;
    }

    [Fact]
    public async Task AgesThatCantBeUsed_SayWhy_AndNothingIsRead()
    {
        var model = Connected(Repo());
        await model.OnNavigatedToAsync();

        model.LogAgeText = "soon";
        await model.ApplyCommand.ExecuteAsync(null);

        Assert.Single(_readWith);
        Assert.StartsWith("Enter the log backup age", model.RpoError);
        Assert.Equal(BackupRpo.Default, new ConnectionSettingsService(_dir).Load().BackupRpo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a file still open: the temp folder keeps it */ }
    }
}
