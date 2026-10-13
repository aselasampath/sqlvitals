using System.IO;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Export / AI's view model on its own (#186): no page, no STA thread. It says what an export will
/// write, chooses the sections and the file, writes the report through <see cref="ExportService"/>
/// over a fake repository, says what the last export did, and opens the file when asked.
/// </summary>
public sealed class ExportViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private readonly FakeDialogService _dialogs = new();
    private readonly FakeFileDialogs _files = new();
    private readonly List<string> _opened = [];

    private static readonly int Count = ExportService.AllGroups.Length;

    // A server that can't be reached: each section says so in the report, which is still written.
    private ExportViewModel Model() =>
        new(new ExportService(new FakeRepository { Unreachable = true }), _dialogs, _files, open: _opened.Add) { FilePath = _dir };

    [Fact]
    public void Opened_EverySectionIsChecked_AndTheVerdictSaysWhereItWillWrite()
    {
        var model = Model();

        Assert.All(model.Sections, s => Assert.True(s.IsChecked));
        Assert.Equal($"{Count} of {Count} selected", model.CheckedCountText);
        Assert.Equal(Path.Combine(_dir, "SqlVitals_Report.txt"), model.FullPathText);
        Assert.Equal((VerdictLevel.Info, $"Ready to export {Count} of {Count} sections"), (model.Verdict!.Level, model.Verdict.Headline));
        Assert.True(model.ExportCommand.CanExecute(null));
    }

    [Fact]
    public void NoSection_OrNoFile_IsWhatTheVerdictSays_AndNothingExports()
    {
        var model = Model();

        model.SelectNoneCommand.Execute(null);
        Assert.Equal($"0 of {Count} selected", model.CheckedCountText);
        Assert.Equal("Nothing to export: no section is selected", model.Verdict!.Headline);
        Assert.False(model.ExportCommand.CanExecute(null));

        model.SelectAllCommand.Execute(null);
        model.FileName = "";
        Assert.Equal("Choose where to save the report", model.Verdict!.Headline);
        Assert.Equal(string.Empty, model.FullPathText);
    }

    [Fact]
    public void Browse_TakesTheFolderAndNameChosen_AndCancelKeepsThem()
    {
        var model = Model();
        _files.Path = @"D:\Reports\prod-sql01.txt";

        model.BrowseCommand.Execute(null);
        Assert.Equal((@"D:\Reports", "prod-sql01.txt"), (model.FilePath, model.FileName));
        Assert.Equal(_dir, _files.Asked.Single().InitialDirectory);

        _files.Path = null;
        model.BrowseCommand.Execute(null);
        Assert.Equal(@"D:\Reports", model.FilePath);
    }

    [Fact]
    public async Task Export_WritesTheReport_SaysWhatItDid_AndOpensIt()
    {
        var model = Model();
        model.SelectNoneCommand.Execute(null);
        model.Sections[0].IsChecked = true;

        await model.ExportCommand.ExecuteAsync(null);

        var path = Path.Combine(_dir, "SqlVitals_Report.txt");
        Assert.Contains("ERROR: No server in tests.", File.ReadAllText(path));
        Assert.StartsWith($"✓  Saved: {path} · 1 group(s)", model.ExportStatus);
        Assert.Equal(VerdictLevel.Ok, model.Verdict!.Level);
        Assert.Equal([path], _opened);
        Assert.False(model.IsExporting);
        Assert.True(model.ShowsProgress);
        Assert.Equal(100, model.Progress);

        // The verdict's Open the file opens it again; another choice makes it about the next export.
        await model.ActCommand.ExecuteAsync(new VerdictAction(ExportVerdict.Open, "Open"));
        Assert.Equal([path, path], _opened);
        model.FileName = "other.txt";
        Assert.Equal(VerdictLevel.Info, model.Verdict!.Level);
    }

    [Fact]
    public async Task AFailedExport_SaysWhy_AndDoesntOpen()
    {
        var model = Model();
        Directory.CreateDirectory(Path.Combine(_dir, "taken"));
        model.FileName = "taken";   // a folder: the report can't be written there

        await model.ActCommand.ExecuteAsync(new VerdictAction(ExportVerdict.Export, "Export now"));

        Assert.StartsWith("✗  Export failed: ", model.ExportStatus);
        Assert.StartsWith("Error: Export error: Export failed:", Assert.Single(_dialogs.Shown));
        Assert.Empty(_opened);
        Assert.False(model.IsExporting);
    }

    [Fact]
    public async Task WhileItWrites_TheChoicesWait()
    {
        var reading = new TaskCompletionSource<IEnumerable<Engine.Models.TopWaitType>>();
        var model = new ExportViewModel(new ExportService(new FakeRepository { GetTopWaitTypesAsync = () => reading.Task }), _dialogs, _files, open: _opened.Add)
        {
            FilePath = _dir,
        };
        model.SelectNoneCommand.Execute(null);
        model.Sections[0].IsChecked = true;   // Top Wait Types

        var writing = model.ExportCommand.ExecuteAsync(null);

        Assert.True(model.IsExporting);
        Assert.False(model.IsIdle);
        Assert.False(model.BrowseCommand.CanExecute(null));
        Assert.False(model.SelectAllCommand.CanExecute(null));
        Assert.False(model.ExportCommand.CanExecute(null));
        reading.SetResult([]);
        await writing;
        Assert.True(model.BrowseCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenAfterExport_Unticked_LeavesTheFileClosed()
    {
        var model = Model();
        model.OpenAfter = false;

        await model.ExportCommand.ExecuteAsync(null);

        Assert.Empty(_opened);
        Assert.True(File.Exists(Path.Combine(_dir, "SqlVitals_Report.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
