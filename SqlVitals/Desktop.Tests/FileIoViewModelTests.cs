using System.IO;
using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Verdicts;
using static SqlVitals.Desktop.Tests.TemplateEverywhereTests;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// File I/O's view model on its own (#187): no page, no STA thread, a fake repository. Its first
/// reading only measures; the second shows the latency between them. It reads every interval while
/// not paused, pauses on a reading it can't take, filters and windows what it measured without
/// reading again, keeps the file selected across readings, applies and saves the thresholds, and
/// starts over.
/// </summary>
public sealed class FileIoViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeUiDispatcher _ui = new();

    private static readonly FileIoReading[] Readings =
    [
        Reading(0, Io(5, "SalesDb", 1, "ROWS", 1_000, 5_000, 100, 200, @"D:\"), Io(5, "SalesDb", 2, "LOG", 0, 0, 1_000, 2_000, @"L:\"),
                   Io(6, "HrDb", 1, "ROWS", 500, 1_000, 10, 10, @"E:\")),
        Reading(10, Io(5, "SalesDb", 1, "ROWS", 1_400, 26_200, 120, 260, @"D:\"), Io(5, "SalesDb", 2, "LOG", 0, 0, 1_500, 4_000, @"L:\"),
                    Io(6, "HrDb", 1, "ROWS", 600, 1_300, 10, 10, @"E:\")),
        Reading(20, Io(5, "SalesDb", 1, "ROWS", 1_500, 27_000, 130, 270, @"D:\"), Io(5, "SalesDb", 2, "LOG", 0, 0, 1_600, 4_100, @"L:\"),
                    Io(6, "HrDb", 1, "ROWS", 700, 1_500, 10, 10, @"E:\")),
    ];

    private sealed record Parts(FileIoViewModel Model, FakeShellStatus Shell, FakeRepository Repo, ConnectionSettingsService Settings);

    private Parts Connected(Func<int, Task<FileIoReading>>? read = null)
    {
        var reads = 0;
        var repo  = new FakeRepository { ReadFileIoAsync = () => read?.Invoke(reads++) ?? Task.FromResult(Readings[Math.Min(reads++, Readings.Length - 1)]) };
        var settings = new ConnectionSettingsService(_dir);
        var model = new FileIoViewModel(repo, settings);
        var shell = new FakeShellStatus();
        model.Connect("FileIo", shell, new RefreshScheduler(_time, _ui));
        return new(model, shell, repo, settings);
    }

    private void Tick(FileIoViewModel model)
    {
        _time.Advance(TimeSpan.FromSeconds(model.IntervalSeconds));
        _ui.RunPosted();
    }

    [Fact]
    public async Task TheFirstReading_OnlyMeasures_TheSecondShowsTheLatency()
    {
        var (model, shell, _, _) = Connected();

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.StartsWith("First reading taken at ", model.ReadNote);
        Assert.Equal(VerdictLevel.Info, model.Verdict!.Level);   // measuring

        Tick(model);   // every 10 s, not paused

        Assert.Equal("Read Timed", shell.Reports[^1]);
        Assert.Equal("3 files", model.CountText);
        Assert.Equal("3 volumes", model.VolumesText);
        Assert.StartsWith("Latency over the 10 s between the last two readings", model.ReadNote);
        Assert.Equal("1 file is slower than your threshold; SalesDb · SalesDb reads in 53.0 ms", model.Verdict!.Headline);
        Assert.Equal("The last interval · every 10 s · data files over 20 ms, log files over 10 ms", model.MeasuringSummary);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task AFilterAndAWindow_ShowWhatWasMeasured_WithoutReadingAgain()
    {
        var reads = 0;
        var (model, _, _, _) = Connected(i => { reads++; return Task.FromResult(Readings[i]); });
        await model.OnNavigatedToAsync();
        await model.RefreshAsync();

        model.FilterIndex = FileIoViewModel.SlowFilter;
        Assert.Equal("1 of 3 files", model.CountText);
        model.FilterIndex = 4;   // log files
        Assert.Equal("SalesDb_log", Assert.Single(model.Files).FileName);
        model.WindowIndex = 1;   // since the first reading
        Assert.Contains("since the first reading", model.ReadNote);

        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task AReading_KeepsTheFileSelected()
    {
        var (model, _, _, _) = Connected();
        await model.OnNavigatedToAsync();
        await model.RefreshAsync();
        model.SelectedFile = model.Files.Single(f => f.DatabaseName == "HrDb");
        var picked = model.SelectedFile;

        await model.RefreshAsync();

        Assert.NotSame(picked, model.SelectedFile);
        Assert.Equal(picked.File.Key, model.SelectedFile!.File.Key);
    }

    [Fact]
    public async Task AReadingItCantTake_PausesIt_UntilStartOver()
    {
        var problem = true;
        var (model, shell, _, _) = Connected(i => Task.FromResult(problem
            ? new FileIoReading(DateTime.Now, DateTime.Now.AddDays(-1), []) { Problem = "Reading file I/O needs VIEW SERVER STATE." }
            : Readings[0]));

        await model.OnNavigatedToAsync();

        Assert.True(model.IsPaused);
        Assert.Equal("Reading file I/O needs VIEW SERVER STATE.", model.NoDataText);
        Assert.True(model.ShowsNoData);
        Assert.Contains("(paused)", model.MeasuringSummary);
        Assert.Equal("Context", shell.Reports[0]);

        problem = false;
        await model.StartOverCommand.ExecuteAsync(null);

        Assert.False(model.IsPaused);
        Assert.StartsWith("First reading taken at ", model.ReadNote);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task Thresholds_AreApplied_AndSaved_OrSayWhyNot()
    {
        var (model, _, _, settings) = Connected();
        await model.OnNavigatedToAsync();
        await model.RefreshAsync();
        Assert.Equal(("20 ms", "10 ms"), (model.DataMsText, model.LogMsText));
        Assert.Equal("(red at 5×)", model.CriticalHint);

        model.DataMsText = "nope";
        model.ApplyThresholdsCommand.Execute(null);
        Assert.True(model.ShowsThresholdError);
        Assert.StartsWith("Enter the data file threshold in milliseconds", model.ThresholdError);

        model.DataMsText = "60";
        model.ApplyThresholdsCommand.Execute(null);
        Assert.False(model.ShowsThresholdError);
        Assert.Equal(new FileLatencyThresholds(60, 10), settings.Load().FileLatencyThresholds);
        Assert.Equal(VerdictLevel.Ok, model.Verdict!.Level);   // 53 ms is under 60
        Assert.Contains("data files over 60 ms", model.MeasuringSummary);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task AnInterval_IsSaved_AndTheTimerFollowsIt()
    {
        var (model, shell, _, settings) = Connected();
        await model.OnNavigatedToAsync();

        model.IntervalIndex = 0;   // 5 s

        Assert.Equal(5, settings.Load().FileIoIntervalSeconds);
        Assert.Equal("Context", shell.Reports[^1]);
        Assert.Equal("5s", model.Countdown);
        model.OnNavigatedFrom();

        // A page opened later starts on it.
        var again = new FileIoViewModel(new FakeRepository(), settings);
        Assert.Equal(5, again.IntervalSeconds);
    }

    [Fact]
    public async Task TheBarsPause_StopsTheTimer_AndTheNotesSaySo()
    {
        var reads = 0;
        var (model, _, _, _) = Connected(i => { reads++; return Task.FromResult(Readings[i]); });
        await model.OnNavigatedToAsync();

        model.SetPaused(true);
        Tick(model);

        Assert.EndsWith("Paused: the top bar's Refresh takes a reading.", model.ReadNote);
        Assert.StartsWith("First reading taken", model.ReadNote);
        Assert.Equal(1, reads);
        Assert.Equal("--", model.Countdown);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task AnError_BeforeAnyReading_IsReportedOnce_AndTheVerdictSaysWhy()
    {
        var (model, shell, _, _) = Connected(_ => Task.FromException<FileIoReading>(new InvalidOperationException("Timeout expired.")));

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the file I/O totals", "Error: Timeout expired."), model.Verdict);
        Assert.Equal("", model.ReadNote);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task AnErrorLater_KeepsTheReadingsSoFar()
    {
        var fail = false;
        var (model, shell, _, _) = Connected(i => fail ? Task.FromException<FileIoReading>(new InvalidOperationException("Timeout expired.")) : Task.FromResult(Readings[i]));
        await model.OnNavigatedToAsync();
        await model.RefreshAsync();
        var verdict = model.Verdict;

        fail = true;
        await model.RefreshAsync();

        Assert.Equal("Error Refresh: Timeout expired.", shell.Reports[^1]);
        Assert.Same(verdict, model.Verdict);
        Assert.Equal(3, model.Files.Count);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task ShowTheSlowFiles_FiltersToThem_AndSelectsTheSlowest()
    {
        var (model, _, _, _) = Connected();
        await model.OnNavigatedToAsync();
        await model.RefreshAsync();
        FileLatency? shown = null;
        model.ShowRow += f => shown = f;

        await model.ActCommand.ExecuteAsync(new VerdictAction(FileIoVerdict.ShowSlow, "Show the slow files"));

        Assert.Equal(FileIoViewModel.SlowFilter, model.FilterIndex);
        Assert.Equal("SalesDb", model.SelectedFile!.DatabaseName);
        Assert.Same(model.SelectedFile, shown);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task Leaving_CancelsTheReading_AndStopsTheTimer()
    {
        var reading = new TaskCompletionSource<FileIoReading>();
        var (model, shell, repo, _) = Connected(_ => reading.Task);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.ReadFileIoAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Equal("--", model.Countdown);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
