using System.IO;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Deadlocks' view model on its own (#187): no page, no STA thread, a fake repository. It reads
/// system_health once, filters what it read by the range, groups the recurring ones, keeps the
/// deadlock selected across a refresh, shows who took part, copies and saves its XML, says what it
/// couldn't read once, and stops reading when the page is left.
/// </summary>
public sealed class DeadlocksViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = DateTime.UtcNow;

    private static FakeRepository Repo(DeadlockHistory? history = null) =>
        new() { GetDeadlockHistoryAsync = () => Task.FromResult(history ?? TemplateRolloutTests.DeadlockHistoryOf(Now)) };

    private sealed record Parts(DeadlocksViewModel Model, FakeShellStatus Shell, FakeDialogService Dialogs, FakeFileDialogs Files, FakeClipboard Clipboard);

    private static Parts Connected(FakeRepository repo)
    {
        var (dialogs, files, clipboard) = (new FakeDialogService(), new FakeFileDialogs(), new FakeClipboard());
        var model = new DeadlocksViewModel(repo, dialogs, files, clipboard);
        var shell = new FakeShellStatus();
        model.Connect("Deadlocks", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return new(model, shell, dialogs, files, clipboard);
    }

    [Fact]
    public async Task Loaded_ListsEveryDeadlock_NewestFirst_AndTheRecurringOnes()
    {
        var (model, shell, _, _, _) = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal(5, model.Deadlocks.Count);
        Assert.Equal("5 deadlocks", model.CountText);
        Assert.Equal("2 patterns, 2 seen more than once", model.PatternCountText);
        Assert.Equal(3, model.Patterns[0].Count);
        Assert.Same(model.Deadlocks[0], model.SelectedDeadlock);
        Assert.StartsWith("Read from system_health's event files (D:\\MSSQL\\Log\\system_health*.xel)", model.ReadNote);
        Assert.StartsWith("system_health's event files · oldest held ", model.SourceSummary);
        Assert.False(model.ShowsProblem);
        Assert.False(model.ShowsNoData);
        Assert.Equal(VerdictLevel.Warning, model.Verdict!.Level);
    }

    [Fact]
    public async Task TheDeadlockSelected_ShowsWhoTookPart_VictimFirst_AndTheirStatement()
    {
        var (model, _, _, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();

        Assert.True(model.HasDeadlock);
        Assert.StartsWith("Deadlock at ", model.DetailHeader);
        Assert.EndsWith(" · 2 sessions · 2 resources", model.DetailHeader);
        Assert.Equal([true, false], model.Processes.Select(p => p.IsVictim));
        Assert.Equal(2, model.Resources.Count);
        Assert.Same(model.Processes[0], model.SelectedProcess);
        Assert.StartsWith("-- SPID 55 (victim) · svc_orders", model.StatementText);
        Assert.Contains("UPDATE dbo.Orders SET Status = 2", model.StatementText);

        model.SelectedProcess = model.Processes[1];
        Assert.StartsWith("-- SPID 61 · svc_billing", model.StatementText);

        model.SelectedDeadlock = null;
        Assert.False(model.HasDeadlock);
        Assert.Equal("Select a deadlock above to see who took part.", model.DetailHeader);
        Assert.Empty(model.Processes);
        Assert.Equal("", model.StatementText);
        Assert.False(model.CopyXmlCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARange_FiltersWhatWasRead_WithoutReadingAgain_AndKeepsTheDeadlockSelected()
    {
        var reads = 0;
        var (model, _, _, _, _) = Connected(new FakeRepository
        {
            GetDeadlockHistoryAsync = () => { reads++; return Task.FromResult(TemplateRolloutTests.DeadlockHistoryOf(Now)); },
        });
        await model.OnNavigatedToAsync();
        model.SelectedDeadlock = model.Deadlocks[1];   // three hours ago

        model.RangeIndex = 1;   // Last 24 hours

        Assert.Equal(1, reads);
        Assert.Equal("4 deadlocks", model.CountText);
        Assert.Equal(model.Deadlocks[1].TimestampUtc, model.SelectedDeadlock!.TimestampUtc);

        model.RangeIndex = 0;   // Last hour: the one selected isn't in it, so the newest
        Assert.Equal("1 deadlock", model.CountText);
        Assert.Same(model.Deadlocks[0], model.SelectedDeadlock);
    }

    [Fact]
    public async Task NoneInTheRange_SaysSo_AndShowAllShowsEveryOne()
    {
        var (model, _, _, _, _) = Connected(Repo(new DeadlockHistory(
            [TemplateRolloutTests.Deadlock(Now.AddDays(-3), "a", "b", "t")], DeadlockSource.RingBuffer, 1, 0, null, null)));
        model.RangeIndex = 0;   // Last hour
        await model.OnNavigatedToAsync();

        Assert.Empty(model.Deadlocks);
        Assert.True(model.ShowsNoData);
        Assert.Equal("No deadlocks in the last hour. system_health holds 1 older: choose a longer range to see them.", model.NoDataText);
        Assert.Equal((VerdictLevel.Ok, "No deadlocks in the last hour"), (model.Verdict!.Level, model.Verdict.Headline));

        await model.ActCommand.ExecuteAsync(new VerdictAction(DeadlocksVerdict.ShowAll, "Show all"));

        Assert.Equal(DeadlocksViewModel.EverythingRange, model.RangeIndex);
        Assert.Single(model.Deadlocks);
    }

    [Fact]
    public async Task Empty_SaysSystemHealthHoldsNone()
    {
        var (model, _, _, _, _) = Connected(Repo(new DeadlockHistory([], DeadlockSource.RingBuffer, 0, 0, null, null)));

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Deadlocks);
        Assert.Null(model.SelectedDeadlock);
        Assert.True(model.ShowsNoData);
        Assert.Equal("system_health holds no deadlock reports: no deadlock has happened in the time it covers.", model.NoDataText);
        Assert.False(model.SaveXdlCommand.CanExecute(null));
    }

    [Fact]
    public async Task NothingReadable_SaysWhy_InTheNoteAndTheGrid()
    {
        var (model, _, _, _, _) = Connected(Repo(DeadlockHistory.Unavailable("Reading system_health needs VIEW SERVER STATE.")));

        await model.OnNavigatedToAsync();

        Assert.True(model.ShowsProblem);
        Assert.Equal("Reading system_health needs VIEW SERVER STATE.", model.NoDataText);
        Assert.Equal("Reading system_health needs VIEW SERVER STATE.", model.SourceSummary);
        Assert.Equal("", model.ReadNote);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var (model, shell, _, _, _) = Connected(new FakeRepository
        {
            GetDeadlockHistoryAsync = () => Task.FromException<DeadlockHistory>(new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the deadlock history", "Error: Timeout expired."), model.Verdict);
        Assert.Equal("", model.ReadNote);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ARefreshThatFails_KeepsWhatWasRead()
    {
        var repo = Repo();
        var (model, shell, _, _, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        var verdict = model.Verdict;

        repo.GetDeadlockHistoryAsync = () => Task.FromException<DeadlockHistory>(new InvalidOperationException("Timeout expired."));
        await model.RefreshAsync();

        Assert.Equal("Error Refresh: Timeout expired.", shell.Reports[^1]);
        Assert.Same(verdict, model.Verdict);
        Assert.Equal(5, model.Deadlocks.Count);
    }

    [Fact]
    public async Task ARefresh_KeepsTheDeadlockSelected()
    {
        var (model, _, _, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        var picked = model.Deadlocks[3];
        model.SelectedDeadlock = picked;

        await model.RefreshAsync();

        Assert.NotSame(picked, model.SelectedDeadlock);   // read again: a new report
        Assert.Equal(picked.TimestampUtc, model.SelectedDeadlock!.TimestampUtc);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<DeadlockHistory>();
        var repo = new FakeRepository { GetDeadlockHistoryAsync = () => reading.Task };
        var (model, shell, _, _, _) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetDeadlockHistoryAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
    }

    [Fact]
    public async Task OpenNewest_ShowsAPatternsNewestDeadlock_OnTheDeadlocksTab()
    {
        var (model, _, _, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        DeadlockReport? shown = null;
        model.ShowRow += r => shown = r;
        model.TabIndex = DeadlocksViewModel.RecurringTab;
        var restock = model.Patterns[1];

        model.OpenNewestCommand.Execute(restock);

        Assert.Equal(DeadlocksViewModel.DeadlocksTab, model.TabIndex);
        Assert.Same(model.SelectedDeadlock, shown);
        Assert.Equal(restock.Newest.TimestampUtc, shown!.TimestampUtc);

        // The verdict's: the most frequent pattern's; and Recurring opens its tab.
        await model.ActCommand.ExecuteAsync(new VerdictAction(DeadlocksVerdict.OpenNewest, "Open the newest"));
        Assert.Equal(model.Patterns[0].Newest.TimestampUtc, model.SelectedDeadlock!.TimestampUtc);
        await model.ActCommand.ExecuteAsync(new VerdictAction(DeadlocksVerdict.Recurring, "Recurring"));
        Assert.Equal(DeadlocksViewModel.RecurringTab, model.TabIndex);
    }

    [Fact]
    public async Task CopyXml_AndSaveAsXdl_TakeTheDeadlockSelected()
    {
        var (model, _, dialogs, files, clipboard) = Connected(Repo());
        await model.OnNavigatedToAsync();

        model.CopyXmlCommand.Execute(null);
        Assert.Equal("<deadlock><victim-list /></deadlock>", clipboard.Text);

        // Cancelled: nothing written, nothing said.
        model.SaveXdlCommand.Execute(null);
        Assert.StartsWith("Deadlock_", Assert.Single(files.Asked).FileName);
        Assert.Empty(dialogs.Shown);

        Directory.CreateDirectory(_dir);
        files.Path = Path.Combine(_dir, "deadlock.xdl");
        model.SaveXdlCommand.Execute(null);
        Assert.Equal("<deadlock><victim-list /></deadlock>", File.ReadAllText(files.Path));
        Assert.StartsWith("Inform: Saved: Deadlock graph saved.", Assert.Single(dialogs.Shown));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
