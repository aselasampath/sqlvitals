using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// DB Storage's view model on its own (#185): no page, no STA thread, a fake repository. It reads
/// through its lifecycle, says what it couldn't read once, stops reading when the page is left,
/// keeps the file selected across a refresh, and lists one database's files while an alert's
/// focus (#145) names it.
/// </summary>
public sealed class DatabaseStorageViewModelTests
{
    private static DatabaseFile DbFile(string db, string type, string logical, decimal sizeMb, decimal maxMb = -1, bool autoShrink = false) =>
        new(db, "ONLINE", "FULL", 160, "Latin1_General_CI_AS", autoShrink, true, true, true, false, false, type, logical,
            $@"D:\MSSQL\{logical}.{(type == "LOG" ? "ldf" : "mdf")}", sizeMb, sizeMb * 0.8m, sizeMb * 0.2m, maxMb, type == "LOG" ? "512 MB" : "10%");

    private static readonly DatabaseFile[] Files =
    [
        DbFile("SalesDb", "ROWS", "SalesDb", 412_000), DbFile("SalesDb", "LOG", "SalesDb_log", 30_720, maxMb: 32_768),
        DbFile("HrDb", "ROWS", "HrDb", 2_048, autoShrink: true), DbFile("HrDb", "LOG", "HrDb_log", 512),
    ];

    private static (IEnumerable<ServerConfigParam>, IEnumerable<DatabaseFile>, IEnumerable<TempDbFileSummary>, IEnumerable<TableStorage>) Storage(
        IEnumerable<DatabaseFile>? files = null, bool config = true) =>
        (config
            ? [new("max degree of parallelism", 8, 0, 32767, "maximum degree of parallelism", true, true),
               new("cost threshold for parallelism", 50, 0, 32767, "cost threshold for parallelism", true, true)]
            : [],
         files ?? Files,
         [new("tempdev", "ROWS", @"T:\tempdb.mdf", 8_192, 2_100, 6_092)],
         [new("dbo", "Orders", 48_000_000, 92_000, 31_000, 123_000, 122_400, 600, 6, "Clustered")]);

    private static FakeRepository Repo() => new() { GetDatabaseStorageAsync = () => Task.FromResult(Storage()) };

    private static (DatabaseStorageViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo)
    {
        var model = new DatabaseStorageViewModel(repo);
        var shell = new FakeShellStatus();
        model.Connect("DbStorage", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return (model, shell);
    }

    [Fact]
    public async Task Loaded_ShowsTheSizes_EveryGrid_AndTheFoldedLine()
    {
        var (model, shell) = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal(("434.84 GB", "404.34 GB", "30.50 GB", "8.00 GB", "8", "50"),
                     (model.TotalSize, model.DataSize, model.LogSize, model.TempDbSize, model.MaxDop, model.CostThreshold));
        Assert.Equal("434.84 GB in all · data 404.34 GB · log 30.50 GB · TempDB 8.00 GB · MAXDOP 8 · cost threshold 50", model.SizesSummary);
        Assert.Equal(4, model.Files.Count);
        Assert.Equal(("Unlimited", $"{32_768m:N2}"),(model.Files[0].MaxSizeMBDisplay, model.Files[1].MaxSizeMBDisplay));
        Assert.Equal("YES", model.Files[2].AutoShrinkDisplay);
        Assert.Single(model.TempDbFiles);
        Assert.Equal(["max degree of parallelism", "cost threshold for parallelism"], model.Config.Select(c => c.ParameterName));
        Assert.Equal("Yes", model.Config[0].IsDynamicDisplay);
        Assert.Single(model.Tables);
        Assert.Equal((VerdictLevel.Warning, "1 file is near its maximum size; SalesDb · SalesDb_log at 94 %"), (model.Verdict!.Level, model.Verdict.Headline));
        Assert.Null(model.Status);
    }

    [Fact]
    public async Task Empty_ShowsNoRows_AndTheSettingsNotRead()
    {
        var (model, _) = Connected(new FakeRepository { GetDatabaseStorageAsync = () => Task.FromResult(Storage(files: [], config: false)) });

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Files);
        Assert.Empty(model.Config);
        Assert.Equal(("0.0 MB", "—", "—"), (model.TotalSize, model.MaxDop, model.CostThreshold));
        Assert.NotNull(model.Verdict);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var (model, shell) = Connected(new FakeRepository
        {
            GetDatabaseStorageAsync = () => Task.FromException<(IEnumerable<ServerConfigParam>, IEnumerable<DatabaseFile>, IEnumerable<TempDbFileSummary>, IEnumerable<TableStorage>)>(
                new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the database storage", "Error: Timeout expired."), model.Verdict);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<(IEnumerable<ServerConfigParam>, IEnumerable<DatabaseFile>, IEnumerable<TempDbFileSummary>, IEnumerable<TableStorage>)>();
        var repo = new FakeRepository { GetDatabaseStorageAsync = () => reading.Task };
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetDatabaseStorageAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
        Assert.Empty(model.Files);
    }

    [Fact]
    public async Task TheFileSelected_IsKeptAcrossARefresh_ByItsDatabaseAndName()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedFile = model.Files.Single(f => f.LogicalFileName == "HrDb_log");
        var rows = model.Files;

        await model.RefreshAsync();

        Assert.Same(rows, model.Files);
        Assert.Equal(("HrDb", "HrDb_log"), (model.SelectedFile!.DatabaseName, model.SelectedFile.LogicalFileName));
        Assert.Contains(model.SelectedFile, model.Files);
    }

    [Fact]
    public async Task ShowTheFile_OpensTheFilesTab_SelectsIt_AndAsksTheViewToShowIt()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedTab = (int)DatabaseStorageViewModel.Tab.Tables;
        object? shown = null;
        model.ShowRow += row => shown = row;

        await model.ActCommand.ExecuteAsync(model.Verdict!.Actions.Single(a => a.Key == DbStorageVerdict.ShowFile));

        Assert.Equal((int)DatabaseStorageViewModel.Tab.Files, model.SelectedTab);
        Assert.Equal("SalesDb_log", model.SelectedFile!.LogicalFileName);
        Assert.Same(model.SelectedFile, shown);
    }

    // ── A log space alert's evidence (#145) ──────────────────────────────

    [Fact]
    public async Task FocusedOnADatabase_ListsItsFilesOnly_UntilShowAll()
    {
        var (model, _) = Connected(Repo());
        model.FocusOn("hrdb");
        Assert.Null(model.FocusNote);
        Assert.Equal("Show all", model.ShowAllText);

        await model.OnNavigatedToAsync();

        Assert.Equal(["HrDb", "HrDb_log"], model.Files.Select(f => f.LogicalFileName));
        Assert.Equal("showing the files of hrdb only", model.FocusNote);
        Assert.Equal("Show all 4 files", model.ShowAllText);

        // Through a refresh too.
        await model.RefreshAsync();
        Assert.Equal(2, model.Files.Count);

        model.FocusOn(null);

        Assert.Equal(4, model.Files.Count);
        Assert.Null(model.FocusNote);
        Assert.Null(model.ShowAllText);
    }

    [Fact]
    public async Task FocusedOnADatabaseWithNoFiles_SaysSo()
    {
        var (model, _) = Connected(Repo());
        model.FocusOn("Gone");

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Files);
        Assert.Equal("Gone has no files on this server now", model.FocusNote);
    }

    [Fact]
    public async Task ShowTheFile_OfAnotherDatabaseThanTheFocus_OpensTheTab_ButSelectsNothing()
    {
        // The verdict is about SalesDb's log; the alert's focus shows HrDb's files only.
        var (model, _) = Connected(Repo());
        model.FocusOn("HrDb");
        await model.OnNavigatedToAsync();
        model.SelectedTab = (int)DatabaseStorageViewModel.Tab.Config;
        var shown = false;
        model.ShowRow += _ => shown = true;

        await model.ActCommand.ExecuteAsync(model.Verdict!.Actions.Single(a => a.Key == DbStorageVerdict.ShowFile));

        Assert.Equal((int)DatabaseStorageViewModel.Tab.Files, model.SelectedTab);
        Assert.Null(model.SelectedFile);
        Assert.False(shown);
    }
}
