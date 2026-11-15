using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The sidebar pinned, collapsed or hidden (#142): Ctrl+B's cycle, the pin, collapsing in a narrow
/// window and coming back, and the state saved for the next start but kept out of the activity log.
/// And the flat pinned sidebar's height (#254): every screen fits a 1000 px window.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class SidebarLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Create() => new(_dir);

    // ── The state machine ─────────────────────────────────────────────────

    [Fact]
    public void CtrlB_GoesPinned_Collapsed_Hidden_AndBackToPinned()
    {
        var layout = new SidebarLayout(SidebarMode.Pinned, windowWidth: 1320);
        var seen = new List<(SidebarMode, double)> { (layout.Shown, layout.Width) };
        for (var i = 0; i < 3; i++)
        {
            layout.Cycle();
            seen.Add((layout.Shown, layout.Width));
            Assert.Equal(layout.Shown, layout.Saved);
        }

        Assert.Equal([(SidebarMode.Pinned, 232), (SidebarMode.Collapsed, 56), (SidebarMode.Hidden, 0), (SidebarMode.Pinned, 232)], seen);
    }

    [Theory]
    [InlineData(SidebarMode.Pinned,    1320, 232)]   // the default window leaves 1088 px for the page
    [InlineData(SidebarMode.Collapsed, 1320, 56)]    // 1264 px
    [InlineData(SidebarMode.Hidden,    1320, 0)]     // 1320 px
    public void EachState_HasItsWidth(SidebarMode mode, double window, double width) =>
        Assert.Equal(width, new SidebarLayout(mode, window).Width);

    [Fact]
    public void ANarrowWindow_ShowsAPinnedSidebarCollapsed_WithoutChangingTheSavedState()
    {
        var layout = new SidebarLayout(SidebarMode.Pinned, windowWidth: 1320);

        Assert.True(layout.SetWindowWidth(1279));
        Assert.Equal(SidebarMode.Collapsed, layout.Shown);
        Assert.Equal(SidebarMode.Pinned, layout.Saved);
        Assert.False(layout.SetWindowWidth(1000));   // still narrow: nothing changes

        // Widening it shows the saved state again.
        Assert.True(layout.SetWindowWidth(1280));
        Assert.Equal(SidebarMode.Pinned, layout.Shown);
    }

    [Fact]
    public void AtStartUp_ANarrowWindow_CollapsesTheSavedPinnedSidebar()
    {
        var layout = new SidebarLayout(SidebarMode.Pinned, windowWidth: 1100);
        Assert.True(layout.IsNarrow);
        Assert.Equal(SidebarMode.Collapsed, layout.Shown);
    }

    [Theory]
    [InlineData(SidebarMode.Collapsed)]
    [InlineData(SidebarMode.Hidden)]
    public void ANarrowWindow_LeavesCollapsedAndHiddenAsTheyAre(SidebarMode saved)
    {
        var layout = new SidebarLayout(saved, windowWidth: 1320);
        layout.SetWindowWidth(1000);
        Assert.Equal(saved, layout.Shown);
    }

    [Fact]
    public void InANarrowWindow_CtrlBGoesOnFromWhatIsShown_AndPinningIsHonoured()
    {
        var layout = new SidebarLayout(SidebarMode.Pinned, windowWidth: 1000);
        Assert.Equal(SidebarMode.Collapsed, layout.Shown);

        // Collapsed on screen, so Ctrl+B hides it rather than appearing to do nothing.
        layout.Cycle();
        Assert.Equal((SidebarMode.Hidden, SidebarMode.Hidden), (layout.Shown, layout.Saved));

        // Pinned on purpose in a narrow window: shown pinned.
        layout.Cycle();
        Assert.Equal((SidebarMode.Pinned, SidebarMode.Pinned), (layout.Shown, layout.Saved));

        // Until the window crosses 1280 px again: wide, then narrow, collapses it once more.
        layout.SetWindowWidth(1400);
        Assert.Equal(SidebarMode.Pinned, layout.Shown);
        layout.SetWindowWidth(1000);
        Assert.Equal(SidebarMode.Collapsed, layout.Shown);
    }

    [Fact]
    public void ThePin_SetsTheStateCtrlBComesBackTo()
    {
        var layout = new SidebarLayout(SidebarMode.Hidden, windowWidth: 1320);
        layout.Choose(SidebarMode.Pinned);
        Assert.Equal((SidebarMode.Pinned, SidebarMode.Pinned), (layout.Shown, layout.Saved));

        layout.Choose(SidebarMode.Collapsed);
        Assert.Equal(SidebarMode.Collapsed, layout.Saved);
        layout.Cycle();
        layout.Cycle();
        Assert.Equal(SidebarMode.Pinned, layout.Shown);
    }

    // ── Saved in settings ─────────────────────────────────────────────────

    [Fact]
    public void ANewInstallation_IsPinned()
    {
        Assert.Equal(SidebarMode.Pinned, Create().Load().SidebarMode);
    }

    [Theory]
    [InlineData(SidebarMode.Collapsed)]
    [InlineData(SidebarMode.Hidden)]
    [InlineData(SidebarMode.Pinned)]
    public void TheState_SurvivesSaveAndLoad(SidebarMode mode)
    {
        Create().SetSidebarMode(SidebarMode.Hidden == mode ? SidebarMode.Collapsed : SidebarMode.Hidden);
        Create().SetSidebarMode(mode);
        Assert.Equal(mode, Create().Load().SidebarMode);
    }

    [Theory]
    [InlineData("Docked")]
    [InlineData("")]
    [InlineData("42")]
    [InlineData(null)]
    public void AnUnknownOrMissingState_ReadsAsPinned(string? value)
    {
        Create().SetSidebarMode(SidebarMode.Hidden);
        var file = Path.Combine(_dir, "settings.dat");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        if (value is null)
            Assert.True(json.Remove("SidebarMode"));   // a file from before 0.58.0
        else
            json["SidebarMode"] = value;
        File.WriteAllText(file, json.ToJsonString());

        Assert.Equal(SidebarMode.Pinned, Create().Load().SidebarMode);
    }

    [Fact]
    public void TheState_IsNotInTheActivityLog_AndIsOnlyWrittenWhenItChanges()
    {
        var service = Create();
        var raised  = new List<ActivityEntry>();
        service.Changed += raised.AddRange;

        service.SetSidebarMode(SidebarMode.Collapsed);
        service.SetSidebarMode(SidebarMode.Hidden);
        var file    = Path.Combine(_dir, "settings.dat");
        var written = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, written.AddMinutes(-1));
        service.SetSidebarMode(SidebarMode.Hidden);

        Assert.Empty(raised);
        Assert.Equal(written.AddMinutes(-1), File.GetLastWriteTimeUtc(file));

        // Not in what the activity log compares, under any name.
        var snapshot = SettingsActivity.Snapshot(service.Load());
        Assert.DoesNotContain(snapshot.Settings, s => s.Name.Contains("sidebar", StringComparison.OrdinalIgnoreCase)
                                                   || s.Name.Contains("menu", StringComparison.OrdinalIgnoreCase)
                                                   || s.Value == nameof(SidebarMode.Hidden));
    }

    [Fact]
    public void NothingAboutTheSidebarsGroups_IsSaved()
    {
        // The groups don't fold any more (#254): no open group is read or written.
        var saved = typeof(ConnectionStore).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(saved, n => n.Contains("Group", StringComparison.Ordinal) && !n.StartsWith("Processes", StringComparison.Ordinal));
        var service = Create();
        service.SetSidebarMode(SidebarMode.Collapsed);
        Assert.DoesNotContain("group", File.ReadAllText(Path.Combine(_dir, "settings.dat")).Replace("GroupByLogin", "", StringComparison.Ordinal),
                              StringComparison.OrdinalIgnoreCase);
    }

    // ── The flat sidebar's height (#254) ──────────────────────────────────

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    [Theory, MemberData(nameof(Themes))]
    public void EveryScreen_FitsA1000PxWindow_WithoutAScrollbar(string theme) => OnSta(theme, 1000, (shell, host) =>
    {
        var scroll = shell.MenuScroll;
        Assert.True(scroll.ExtentHeight <= scroll.ViewportHeight,
                    $"the sidebar needs {scroll.ExtentHeight} px and has {scroll.ViewportHeight} " +
                    $"(panel {((FrameworkElement)scroll.Content).ActualHeight}/{((FrameworkElement)scroll.Content).DesiredSize.Height}, scroll {scroll.ActualHeight}, bar {scroll.ComputedVerticalScrollBarVisibility}): " +
                    $"server card {shell.Menu.ServerHeader.ActualHeight}, Recent {shell.Menu.RecentRow.ActualHeight}, " +
                    $"Alerts {shell.Menu.Button("Alerts").ActualHeight}, filter {shell.Menu.FilterBar.ActualHeight}; " +
                    string.Join(" ", ((Panel)scroll.Content).Children.OfType<FrameworkElement>()
                        .Where(c => c.Visibility == Visibility.Visible)
                        .Select(c => $"{c.GetType().Name}:{c.ActualHeight + c.Margin.Top + c.Margin.Bottom:0}")));
        Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
        Assert.Equal(SidebarLayout.PinnedWidth, shell.ColumnDefinitions[0].ActualWidth);
        // Every screen, the captions and the full server card are laid out, the Recent row too.
        Assert.All(NavMenu.Groups.SelectMany(g => g.Pages), p => Assert.Equal(NavSidebar.RowHeight, shell.Menu.Button(p.Tag).ActualHeight));
        Assert.Equal(3, shell.Menu.RecentChips.Count());
        Assert.True(shell.Menu.ServerVerdict.ActualHeight > 0);
        Render(host, theme, 1000);
    });

    [Theory, MemberData(nameof(Themes))]
    public void InAShorterWindow_TheMiddleScrolls_AndSettingsStaysAtTheFoot(string theme) => OnSta(theme, 800, (shell, host) =>
    {
        var scroll = shell.MenuScroll;
        Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);
        Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);

        var settings = shell.Menu.Button("Settings");
        var top = settings.TranslatePoint(new Point(0, 0), host).Y;
        Assert.InRange(top + settings.ActualHeight, 800 - 20, 800);
        Assert.True(scroll.TranslatePoint(new Point(0, scroll.ActualHeight), host).Y <= top);
        // The filter box stays at the top, not scrolled away.
        Assert.True(shell.Menu.FilterBar.TranslatePoint(new Point(0, 0), host).Y < scroll.TranslatePoint(new Point(0, 0), host).Y);
        Render(host, theme, 800);
    });

    // The shell pinned at 1320 × height, PROD-SQL01 open on Query Store with its card at its
    // fullest (a two-line verdict), three findings and three Recent chips.
    private static void OnSta(string theme, double height, Action<WorkspaceShell, FrameworkElement> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = (ResourceDictionary)Application.LoadComponent(
                    new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative));
                var shell = new WorkspaceShell { Animate = false };
                var page  = new Border { Background = (Brush)resources["BgDeep"] };
                Grid.SetColumn(page, 1);
                shell.Children.Add(page);
                var host = new Border { Resources = resources, Child = shell, Background = (Brush)resources["BgDeep"] };

                var server = new ConnectionSelectorItem(new ConnectionSettings { Server = "prod-sql01.corp.example", Name = "PROD-SQL01", Database = "SalesDb" });
                var score  = HealthScore.From(
                [
                    new HealthReading(HealthIndicator.Blocking, HealthLevel.Critical, 600, new HealthThreshold(30, 300), null),
                    new HealthReading(HealthIndicator.Cpu, HealthLevel.Warning, 85, new HealthThreshold(75, 90), null),
                    new HealthReading(HealthIndicator.BackupsPastRpo, HealthLevel.Warning, 3, new HealthThreshold(1, 5), null),
                ], DateTime.Now);
                typeof(ConnectionSelectorItem).GetProperty(nameof(ConnectionSelectorItem.Score))!.SetValue(server, score);
                typeof(ConnectionSelectorItem).GetProperty(nameof(ConnectionSelectorItem.IsMonitoring))!.SetValue(server, true);

                shell.ViewModel.Restore(SidebarMode.Pinned, 1320);
                shell.ViewModel.ShowServer(server);
                shell.ViewModel.Current = "QueryStore";
                shell.ViewModel.Recent  = ["Processes", "Waits", "Backups"];
                shell.SetCount("Alerts", 3, "3 active alerts");
                shell.VersionText.Text = "v0.89.0";
                host.Measure(new Size(1320, height));
                host.Arrange(new Rect(0, 0, 1320, height));
                host.UpdateLayout();

                test(shell, host);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    // The sidebar alone, to look at, under %TEMP%\SqlVitalsTests\sidebar.
    private static void Render(FrameworkElement host, string theme, double height)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "sidebar");
        Directory.CreateDirectory(dir);
        var bitmap = new RenderTargetBitmap((int)SidebarLayout.PinnedWidth, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"pinned-{height}-{theme}.png"));
        encoder.Save(file);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
