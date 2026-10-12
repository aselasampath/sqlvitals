using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The MVVM rules (#181, written down in <c>docs/Architecture.md</c>, "MVVM") checked over the
/// app's sources. Each rule has an allowlist of today's offenders that may only shrink: a new
/// offence fails with its file and line, and so does an entry that no longer offends, so the list
/// is cut as each page moves to its view model (#180).
/// </summary>
public sealed class MvvmGuardTests
{
    // ── Allowlists: today's offenders. Remove an entry (or lower a count) when it's fixed. ─────

    /// <summary>View models that refer to the view, and to what: none since #182 took <c>PageViewModel.ShowError</c> off <c>MainWindow</c>.</summary>
    private static readonly Dictionary<string, string[]> ViewReferencesAllowed = new();

    /// <summary>Page code that reads through a repository itself.</summary>
    private static readonly string[] RepositoryReferencesAllowed =
    [
        "Pages/AgentJobsPage.xaml.cs",
        "Pages/ApplicationConnectionsPage.xaml.cs",
        "Pages/BackupsPage.xaml.cs",
        "Pages/ConfigChecksPage.xaml.cs",
        "Pages/DatabaseStoragePage.xaml.cs",
        "Pages/DeadlocksPage.xaml.cs",
        "Pages/FileIoPage.xaml.cs",
        "Pages/IndexHealthPage.xaml.cs",
        "Pages/LiveMetricsDashboardPage.xaml.cs",
        "Pages/MemoryPage.xaml.cs",
        "Pages/OverviewPage.xaml.cs",
        "Pages/PerfmonPage.xaml.cs",
        "Pages/PlanCacheHealthPage.xaml.cs",
        "Pages/ProcessesPage.xaml.cs",
        "Pages/QueryRegressionsPage.xaml.cs",
        "Pages/QueryStorePage.xaml.cs",
        "Pages/ResourceQueriesPage.xaml.cs",
        "Pages/SecurityAuditPage.xaml.cs",
        "Pages/SpTracePage.xaml.cs",
        "Pages/StaleStatisticsPage.xaml.cs",
        "Pages/TempDbPage.xaml.cs",
        "Pages/WaitsPage.xaml.cs",
    ];

    /// <summary>How many <c>Click=</c> each page's XAML has.</summary>
    private static readonly Dictionary<string, int> ClicksAllowed = new()
    {
        ["Pages/AgentJobsPage.xaml"]                     = 1,
        ["Pages/BackupsPage.xaml"]                       = 2,
        ["Pages/ConfigChecksPage.xaml"]                  = 2,
        ["Pages/DeadlocksPage.xaml"]                     = 4,
        ["Pages/ExportPage.xaml"]                        = 4,
        ["Pages/FileIoPage.xaml"]                        = 2,
        ["Pages/HomePage.xaml"]                          = 4,
        ["Pages/IndexHealthPage.xaml"]                   = 4,
        ["Pages/LiveMetricsDashboardPage.xaml"]          = 4,
        ["Pages/OverviewPage.xaml"]                      = 14,
        ["Pages/PerfmonPage.xaml"]                       = 2,
        ["Pages/QueryRegressionsPage.xaml"]              = 3,
        ["Pages/QueryStorePage.xaml"]                    = 7,
        ["Pages/ResourceQueriesPage.xaml"]               = 6,
        ["Pages/SecurityAuditPage.xaml"]                 = 2,
        ["Pages/Settings/ActivityCategory.xaml"]         = 1,
        ["Pages/Settings/AlertsCategory.xaml"]           = 8,
        ["Pages/Settings/AppearanceCategory.xaml"]       = 1,
        ["Pages/Settings/ConnectionEditor.xaml"]         = 11,
        ["Pages/Settings/ConnectionsCategory.xaml"]      = 4,
        ["Pages/Settings/MonitoringCategory.xaml"]       = 2,
        ["Pages/Settings/NotificationAreaCategory.xaml"] = 1,
        ["Pages/Settings/ThresholdsCategory.xaml"]       = 3,
        ["Pages/SettingsPage.xaml"]                      = 1,
        ["Pages/SpTracePage.xaml"]                       = 2,
        ["Pages/StaleStatisticsPage.xaml"]               = 1,
        ["Pages/WaitsPage.xaml"]                         = 2,
    };

    /// <summary>Pages whose code-behind still reads (#183): <see cref="Pages.ICodeBehindPage"/>. Each wave of #180 takes its pages off.</summary>
    private static readonly string[] CodeBehindPagesAllowed =
    [
        "AgentJobsPage",
        "AlertsPage",
        "ApplicationConnectionsPage",
        "BackupsPage",
        "ConfigChecksPage",
        "DatabaseStoragePage",
        "DeadlocksPage",
        "ExportPage",
        "FileIoPage",
        "HomePage",
        "IndexHealthPage",
        "LiveMetricsDashboardPage",
        "MemoryPage",
        "OverviewPage",
        "PerfmonPage",
        "PlanCacheHealthPage",
        "ProcessesPage",
        "QueryRegressionsPage",
        "QueryStorePage",
        "ResourceQueriesPage",
        "SecurityAuditPage",
        "SpTracePage",
        "StaleStatisticsPage",
        "TempDbPage",
        "WaitsPage",
    ];

    // ── View models stay out of the view ─────────────────────────────────

    [Fact]
    public void ViewModels_DoNotReferToTheView()
    {
        var broken = ViewModelOffences()
            .Where(o => !(ViewReferencesAllowed.TryGetValue(o.File, out var names) && names.Contains(o.Name)))
            .ToList();

        Fail(broken, "A view model may not refer to System.Windows.Controls, MessageBox, MainWindow, Application or a Dispatcher " +
                     "(docs/Architecture.md, MVVM). Ask a service for it instead:");
    }

    [Fact]
    public void ViewModels_AllowlistOnlyNamesOffenders()
    {
        var offences = ViewModelOffences().Select(o => (o.File, o.Name)).ToHashSet();
        var stale = ViewReferencesAllowed
            .SelectMany(entry => entry.Value.Select(name => (File: entry.Key, Name: name)))
            .Where(entry => !offences.Contains(entry))
            .Select(entry => $"Desktop/{entry.File} no longer refers to {entry.Name}: take it off ViewReferencesAllowed.")
            .ToList();

        Fail(stale, "The allowlist only shrinks:");
    }

    private static IReadOnlyList<Offence> ViewModelOffences() => LazyViewModelOffences.Value;

    private static readonly Lazy<IReadOnlyList<Offence>> LazyViewModelOffences = new(() =>
        Models("ViewModels/").SelectMany(m => MvvmRules.ViewReferences(m.Model, m.File)).ToList());

    // ── Page code leaves the repositories to view models ─────────────────

    [Fact]
    public void PageCode_DoesNotReadThroughARepository()
    {
        var broken = RepositoryOffences().Where(o => !RepositoryReferencesAllowed.Contains(o.File)).ToList();

        Fail(broken, "Page code may not use a repository (docs/Architecture.md, MVVM); its view model reads the data:");
    }

    [Fact]
    public void PageCode_AllowlistOnlyNamesOffenders()
    {
        var offending = RepositoryOffences().Select(o => o.File).ToHashSet();
        var stale = RepositoryReferencesAllowed
            .Where(file => !offending.Contains(file))
            .Select(file => $"Desktop/{file} no longer uses a repository: take it off RepositoryReferencesAllowed.")
            .ToList();

        Fail(stale, "The allowlist only shrinks:");
    }

    private static IReadOnlyList<Offence> RepositoryOffences() => LazyRepositoryOffences.Value;

    private static readonly Lazy<IReadOnlyList<Offence>> LazyRepositoryOffences = new(() =>
        Models("Pages/").SelectMany(m => MvvmRules.RepositoryReferences(m.Model, m.File)).ToList());

    // ── Only the shell refers to the main window (#166) ──────────────────

    [Fact]
    public void PagesControlsWindowsAndHelpers_DoNotReferToMainWindow()
    {
        var broken = new[] { "Pages/", "Controls/", "Windows/", "Helpers/" }
            .SelectMany(Models)
            .SelectMany(m => MvvmRules.WindowReferences(m.Model, m.File))
            .ToList();

        Fail(broken, "Only the shell refers to MainWindow (docs/Architecture.md, The composition root): " +
                     "report to IShellStatus, open pages through INavigator, and ask the constructor for services:");
    }

    [Fact]
    public void WindowReferences_FindsTheWindow_ByNameMemberOrType()
    {
        AssertFinds("""
            using System.Windows;

            namespace SqlVitals.Desktop { public class MainWindow : Window { public static void Report(object page) { } public void Open(string tag) { } } }

            namespace SqlVitals.Desktop.Pages
            {
                /// <summary>Not <see cref="MainWindow"/>: comments don't count.</summary>
                public class SomePage : System.Windows.Controls.Page
                {
                    public void Read() => MainWindow.Report(this);                          // ✗ MainWindow
                    public void Open() { if (Window.GetWindow(this) is MainWindow main)     // ✗ MainWindow
                                             main.Open("Settings"); }                       // ✗ MainWindow
                    public Window? Owner => Application.Current.MainWindow;
                    public string Name => "MainWindow";
                }
            }
            """, MvvmRules.WindowReferences);
    }

    // ── Click= in the pages' XAML doesn't grow ───────────────────────────

    [Fact]
    public void PageXaml_ClickHandlersDoNotGrow() =>
        Fail(Grown(ClickOffences(), ClicksAllowed),
             "A page's XAML has more Click= than allowed; bind a command instead (docs/Architecture.md, MVVM):");

    [Fact]
    public void PageXaml_AllowlistShrinksWithIt() =>
        Fail(Shrunk(ClickOffences(), ClicksAllowed, nameof(ClicksAllowed)), "The allowlist only shrinks:");

    private static IEnumerable<Offence> ClickOffences() =>
        DesktopSource.Files("*.xaml", "Pages")
            .SelectMany(path => MvvmRules.Clicks(File.ReadAllText(path), DesktopSource.Relative(path)));

    // ── async void only in event handlers ────────────────────────────────

    // None left since #169: the rule has no allowlist.
    [Fact]
    public void AsyncVoid_OnlyInEventHandlers() =>
        Fail(InEveryFile(MvvmRules.AsyncVoid),
             "async void outside an event handler: its caller can't await it or catch what it throws. Return a Task, or use an async command:");

    // ── An async void handler catches what it awaits (#169) ──────────────

    [Fact]
    public void AsyncVoidHandlers_CatchWhatTheyAwait() =>
        Fail(InEveryFile(MvvmRules.UncaughtAwaits),
             "An async void event handler awaits something that can throw, which would reach the dispatcher instead of the user. " +
             "Wrap it in try/catch (Exception) and report it (IShellStatus, the page's status, or AppLog), or await a [ReportsItsErrors] " +
             "method such as ViewModel.RefreshAsync():");

    // A rule over every file of the app, with the file's XAML when it is code-behind.
    private static List<Offence> InEveryFile(Func<SemanticModel, string, string?, IEnumerable<Offence>> rule) =>
        Models("").SelectMany(m =>
        {
            var xaml = m.Path.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) && File.Exists(m.Path[..^3])
                ? File.ReadAllText(m.Path[..^3]) : null;
            return rule(m.Model, m.File, xaml);
        }).ToList();

    // ── No page runs its own timer (#169) ────────────────────────────────

    [Fact]
    public void Pages_DontMakeTheirOwnDispatcherTimer() =>
        Fail(DispatcherTimerOffences(),
             "A page makes its own DispatcherTimer: use its view model's clock instead (ReadEvery, ReadSoon, TickWhileShown) or IRefreshScheduler:");

    private static List<Offence> DispatcherTimerOffences() =>
        Models("Pages/").SelectMany(m => m.Model.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<BaseObjectCreationExpressionSyntax>()
            .Where(n => m.Model.GetTypeInfo(n).Type is { Name: "DispatcherTimer" })
            .Select(n => new Offence(m.File, n.GetLocation().GetLineSpan().StartLinePosition.Line + 1, "DispatcherTimer", "new DispatcherTimer")))
            .ToList();

    // ── Where view models live ───────────────────────────────────────────

    [Fact]
    public void ViewModels_OneFileEach_NamedForTheirPage()
    {
        var pages    = DesktopSource.Files("*Page.xaml", "Pages").Select(p => Path.GetFileName(p)[..^"Page.xaml".Length]).ToHashSet();
        var settings = DesktopSource.Files("*.xaml", "Pages/Settings").Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet();
        var broken   = Models("ViewModels/").SelectMany(m => MvvmRules.ViewModelLayout(m.Model, m.File, pages, settings)).ToList();

        Fail(broken, "View models are one file each, named for their page (docs/Architecture.md, MVVM):");
    }

    // ── Pages read through their view models ─────────────────────────────

    [Fact]
    public void CodeBehindPages_DoNotGrow()
    {
        var broken = CodeBehindPages().Where(name => !CodeBehindPagesAllowed.Contains(name))
            .Select(name => $"{name} reads in its code-behind (ICodeBehindPage): load in its view model's LoadAsync instead.")
            .ToList();

        Fail(broken, "A new page reads through its view model (docs/Architecture.md, Adding a page):");
    }

    [Fact]
    public void CodeBehindPages_AllowlistShrinksWithIt()
    {
        var pages = CodeBehindPages().ToHashSet();
        var stale = CodeBehindPagesAllowed.Where(name => !pages.Contains(name))
            .Select(name => $"{name} no longer reads in its code-behind: take it off CodeBehindPagesAllowed.")
            .ToList();

        Fail(stale, "The allowlist only shrinks:");
    }

    private static IEnumerable<string> CodeBehindPages() =>
        typeof(MainWindow).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Pages.ICodeBehindPage).IsAssignableFrom(t))
            .Select(t => t.Name);

    // ── The checks themselves ────────────────────────────────────────────

    // A line marked "// ✗ Name" is where the check must find Name; no other line may be found.
    private static void AssertFinds(string code, Func<SemanticModel, string, IEnumerable<Offence>> check)
    {
        var tree     = DesktopSource.Parse(code, "Snippet.cs");
        var model    = DesktopSource.Compile([tree]).GetSemanticModel(tree);
        var expected = code.Split('\n')
            .Select((line, i) => (Line: i + 1, Mark: line.Split("// ✗ ") is [_, var name] ? name.Trim() : null))
            .Where(l => l.Mark is not null)
            .SelectMany(l => l.Mark!.Split(", ").Select(name => $"{l.Line}: {name}"));

        Assert.Equal(expected.Order(), check(model, "Snippet.cs").Select(o => $"{o.Line}: {o.Name}").Order());
    }

    [Fact]
    public void ViewReferences_FindsTheView_ByNameMemberOrType()
    {
        AssertFinds("""
            using System.Windows;
            using System.Windows.Controls;                                                  // ✗ System.Windows.Controls
            using System.Windows.Threading;

            namespace SqlVitals.Desktop { public static class MainWindow { public static string Format(Exception e) => e.Message; } }

            namespace SqlVitals.Desktop.ViewModels
            {
                public class App : Application { }                                          // ✗ Application
                /// <summary>Not <see cref="MessageBox"/>: comments don't count.</summary>
                public class SomeViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
                {
                    public void Ask() => MessageBox.Show("Sure?");                         // ✗ MessageBox
                    public object? Owner => App.Current.MainWindow;                         // ✗ Application
                    public string Text(Exception e) => MainWindow.Format(e);                // ✗ MainWindow
                    public void Later(DispatcherObject o) => o.Dispatcher.Invoke(() => { }); // ✗ Dispatcher
                    public string Name => "MainWindow";
                    public SqlVitals.Engine.Verdicts.PageVerdict? Verdict { get; set; }
                    public LiveChartsCore.Defaults.ObservableValue Point { get; } = new(1);
                }
            }
            """, MvvmRules.ViewReferences);
    }

    [Fact]
    public void RepositoryReferences_FindsTheType_ItsMembers_AndValuesOfIt()
    {
        AssertFinds("""
            using SqlVitals.Engine.Repositories;

            namespace SqlVitals.Desktop.Pages;

            public class Session { public IWaitStatsRepository Repository { get; } = null!; }            // ✗ IWaitStatsRepository

            /// <summary>Reads through <see cref="ITempDbRepository"/>: comments don't count.</summary>
            public class SomePage
            {
                private readonly ITempDbRepository _repo = null!;                                      // ✗ ITempDbRepository
                public async Task Load(Session session) => await session.Repository.GetProcessesAsync(); // ✗ IWaitStatsRepository, ISessionRepository
                public string Name => "Repository";
            }
            """, MvvmRules.RepositoryReferences);
    }

    [Fact]
    public void AsyncVoid_AllowsEventHandlers_FindsTheRest()
    {
        AssertFinds("""
            namespace SqlVitals.Desktop.Pages;

            public partial class SomePage : System.Windows.Controls.UserControl
            {
                public event EventHandler? Changed;

                public SomePage()
                {
                    Changed += OnChanged;
                    Changed += async (_, _) => await Task.Yield();
                    Loaded  += new System.Windows.RoutedEventHandler(OnLoaded);
                    Run(async () => await Task.Yield());                                    // ✗ async void
                    _ = Task.Run(async () => await Task.Yield());
                    async void Later() => await Task.Yield();                               // ✗ async void
                    Loaded += OnLoadedHere;
                    async void OnLoadedHere(object sender, System.Windows.RoutedEventArgs e) => await Task.Yield();
                }

                private async void OnChanged(object? sender, EventArgs e) => await Task.Yield();
                private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e) => await Task.Yield();
                private async void Save_Click(object sender, System.Windows.RoutedEventArgs e) => await Task.Yield();
                private async void Panel_Click(object sender, System.Windows.RoutedEventArgs e) => await Task.Yield();
                private async void Open() => await Task.Yield();                            // ✗ async void
                private async void Refresh() => await Task.Yield();                         // ✗ async void
                private async Task LoadAsync() => await Task.Yield();
                private static void Run(Action action) => action();
            }
            """, (model, file) => MvvmRules.AsyncVoid(model, file, xaml: """
                <UserControl>
                    <Button Click="Save_Click"/>
                    <StackPanel ButtonBase.Click="Panel_Click"/>
                    <Button x:Name="Refresh" Content="Open"/>
                </UserControl>
                """));
    }

    [Fact]
    public void UncaughtAwaits_InHandlersOnly_CatchAllOrReportsItsErrors()
    {
        AssertFinds("""
            namespace SqlVitals.Desktop.Pages;

            [AttributeUsage(AttributeTargets.Method)] public sealed class ReportsItsErrorsAttribute : Attribute;

            public partial class SomePage : System.Windows.Controls.UserControl
            {
                public SomePage()
                {
                    Loaded += async (_, _) => await Task.Yield();                         // ✗ uncaught await
                    Loaded += async (_, _) => await Reads();
                    Loaded += OnLoaded;
                    Loaded += OnTried;
                    Loaded += OnFiltered;
                    Loaded += OnNarrow;
                    Loaded += OnNested;
                }

                [ReportsItsErrors] private static Task Reads() => Task.CompletedTask;

                private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
                {
                    await Reads();
                    await Task.Delay(1);                                                    // ✗ uncaught await
                }

                private async void OnTried(object sender, System.Windows.RoutedEventArgs e)
                {
                    try { await Task.Delay(1); }
                    catch (Exception ex) { _ = ex; }
                    try { await Task.Delay(2); }
                    catch { }
                    try { } finally { await Task.Delay(3); }                               // ✗ uncaught await
                }

                private async void OnFiltered(object sender, System.Windows.RoutedEventArgs e)
                {
                    try { await Task.Delay(1); }                                            // ✗ uncaught await
                    catch (Exception ex) when (ex is TimeoutException) { }
                }

                private async void OnNarrow(object sender, System.Windows.RoutedEventArgs e)
                {
                    try { await Task.Delay(1); }                                            // ✗ uncaught await
                    catch (InvalidOperationException) { }
                }

                private async void OnNested(object sender, System.Windows.RoutedEventArgs e)
                {
                    try
                    {
                        Func<Task> later = async () => await Task.Delay(1);
                        await later();
                    }
                    catch (Exception) { }
                }

                // Not a handler: AsyncVoid's to find, not this rule's.
                private async void Open() => await Task.Delay(1);
                private async Task LoadAsync() => await Task.Delay(1);
            }
            """, (model, file) => MvvmRules.UncaughtAwaits(model, file, xaml: null));
    }

    [Fact]
    public void ViewModelLayout_FindsMisnamedAndMisplacedViewModels()
    {
        var files = new Dictionary<string, string>
        {
            ["ViewModels/PageViewModel.cs"]                    = "namespace SqlVitals.Desktop.ViewModels; public abstract class PageViewModel { }",
            ["ViewModels/ChartPageViewModel.cs"]               = "namespace SqlVitals.Desktop.ViewModels; public abstract class ChartPageViewModel : PageViewModel { }",
            ["ViewModels/LostViewModel.cs"]                    = "namespace SqlVitals.Desktop.ViewModels; public sealed class LostViewModel : PageViewModel { }",
            ["ViewModels/Pages/BackupsViewModel.cs"]           = "namespace SqlVitals.Desktop.ViewModels.Pages; public sealed class BackupsViewModel : PageViewModel { }",
            ["ViewModels/Pages/BackupsRowViewModel.cs"]        = "namespace SqlVitals.Desktop.ViewModels.Pages; public sealed class BackupsRowViewModel { }",
            ["ViewModels/Pages/PerfmonViewModel.cs"]           = "namespace SqlVitals.Desktop.ViewModels.Pages; public sealed class PerfmonViewModel : ChartPageViewModel { } public sealed class Extra { }",
            ["ViewModels/Pages/Foo.cs"]                        = "namespace SqlVitals.Desktop.ViewModels.Pages; public sealed class Foo : PageViewModel { }",
            ["ViewModels/Settings/AlertsCategoryViewModel.cs"] = "namespace SqlVitals.Desktop.ViewModels.Settings; public sealed class AlertsCategoryViewModel { }",
            ["ViewModels/Settings/Misc.cs"]                    = "namespace SqlVitals.Desktop.ViewModels.Settings; public sealed class Misc { }",
        };
        var trees       = files.Select(f => DesktopSource.Parse(f.Value, f.Key)).ToList();
        var compilation = DesktopSource.Compile(trees);

        var found = trees
            .SelectMany(tree => MvvmRules.ViewModelLayout(compilation.GetSemanticModel(tree), tree.FilePath,
                                                          new HashSet<string> { "Backups", "Perfmon" }, new HashSet<string> { "AlertsCategory" }))
            .Select(o => o.File);

        // Foo.cs twice: not named for a page, and a page's view model named so.
        Assert.Equal(["ViewModels/LostViewModel.cs", "ViewModels/Pages/Foo.cs", "ViewModels/Pages/Foo.cs",
                      "ViewModels/Pages/PerfmonViewModel.cs", "ViewModels/Settings/Misc.cs"], found.Order());
    }

    [Fact]
    public void Clicks_CountsClickAttributes_NotCommentsOrOtherEvents()
    {
        const string xaml = """
            <UserControl>
                <!-- <Button Click="Old"/> -->
                <Button Click="Save"/>
                <Grid MouseDoubleClick="Open"/>
                <MenuItem Header="Copy"
                          Click = "Copy"/>
                <StackPanel ButtonBase.Click="Any"/>
            </UserControl>
            """;

        Assert.Equal([3, 6, 7], MvvmRules.Clicks(xaml, "Some.xaml").Select(o => o.Line));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Each app file under <paramref name="folder"/> with its semantic model.</summary>
    private static IEnumerable<(SemanticModel Model, string File, string Path)> Models(string folder) =>
        DesktopSource.Compilation.SyntaxTrees
            .Where(tree => tree.FilePath.Length > 0)
            .Select(tree => (Tree: tree, File: DesktopSource.Relative(tree.FilePath)))
            .Where(t => t.File.StartsWith(folder, StringComparison.Ordinal))
            .Select(t => (DesktopSource.Compilation.GetSemanticModel(t.Tree), t.File, t.Tree.FilePath));

    // The files with more offences than allowed, each offence listed.
    private static List<string> Grown(IEnumerable<Offence> offences, IReadOnlyDictionary<string, int> allowed) =>
        offences.GroupBy(o => o.File)
            .Where(g => g.Count() > allowed.GetValueOrDefault(g.Key))
            .SelectMany(g => g.Select(o => o.ToString())
                              .Prepend($"Desktop/{g.Key}: {g.Count()}, allowed {allowed.GetValueOrDefault(g.Key)}"))
            .ToList();

    // The entries that allow more than the file has.
    private static List<string> Shrunk(IEnumerable<Offence> offences, IReadOnlyDictionary<string, int> allowed, string list)
    {
        var counts = offences.GroupBy(o => o.File).ToDictionary(g => g.Key, g => g.Count());
        return allowed
            .Where(entry => counts.GetValueOrDefault(entry.Key) < entry.Value)
            .Select(entry => counts.GetValueOrDefault(entry.Key) is var now && now == 0
                ? $"Desktop/{entry.Key} has none now: take it off {list}."
                : $"Desktop/{entry.Key} has {now} now: lower it from {entry.Value} to {now} in {list}.")
            .ToList();
    }

    private static void Fail(IReadOnlyCollection<string> lines, string why)
    {
        if (lines.Count > 0)
            Assert.Fail(why + Environment.NewLine + string.Join(Environment.NewLine, lines));
    }

    private static void Fail(IReadOnlyCollection<Offence> offences, string why) =>
        Fail(offences.Select(o => o.ToString()).ToList(), why);
}
