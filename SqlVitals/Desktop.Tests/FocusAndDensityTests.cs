using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Focus mode's window placement and sidebar, and grid density (#148), with a page rendered in
/// both densities (to %TEMP%\SqlVitalsTests\grid-density).
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class FocusAndDensityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private static readonly string RenderDir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "grid-density");

    private ConnectionSettingsService Settings() => new(_dir);

    // ── Focus mode ───────────────────────────────────────────────────────

    [Fact]
    public void TheSidebar_IsHiddenInFocus_AndTheChosenStateKept()
    {
        var layout = new SidebarLayout(SidebarMode.Pinned, windowWidth: 1100) { InFocus = true };
        Assert.Equal(SidebarMode.Hidden, layout.Shown);
        Assert.Equal(SidebarMode.Pinned, layout.Saved);
        Assert.Equal(0, layout.Width);

        layout.InFocus = false;
        // Back as it was: pinned, shown collapsed in a narrow window.
        Assert.Equal(SidebarMode.Collapsed, layout.Shown);
    }

    [Fact]
    public void Focus_MakesTheWindowFullScreen_AndExitPutsItBackWhereItWas() => UiThread.Run(() =>
    {
        var window = new Window { Left = 120, Top = 80, Width = 1100, Height = 700, WindowStyle = WindowStyle.SingleBorderWindow,
                                  ResizeMode = ResizeMode.CanResize };
        var before = WindowPlacement.Capture(window);
        Assert.Equal(new WindowPlacement(WindowState.Normal, WindowStyle.SingleBorderWindow, ResizeMode.CanResize, new Rect(120, 80, 1100, 700)), before);

        before.FullScreen().ApplyTo(window);
        Assert.Equal((WindowState.Maximized, WindowStyle.None, ResizeMode.NoResize), (window.WindowState, window.WindowStyle, window.ResizeMode));
        Assert.True(WindowPlacement.Capture(window).IsFullScreen);

        before.ApplyTo(window);
        Assert.Equal(before, WindowPlacement.Capture(window));
    });

    [Fact]
    public void AMaximizedWindow_ComesBackMaximized() => UiThread.Run(() =>
    {
        var window = new Window { WindowState = WindowState.Maximized };
        var before = WindowPlacement.Capture(window);

        before.FullScreen().ApplyTo(window);
        before.ApplyTo(window);

        Assert.Equal((WindowState.Maximized, WindowStyle.SingleBorderWindow, ResizeMode.CanResize),
                     (window.WindowState, window.WindowStyle, window.ResizeMode));
        Assert.False(WindowPlacement.Capture(window).IsFullScreen);
    });

    // ── Grid density ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Compact", GridDensity.Compact)]
    [InlineData("compact", GridDensity.Compact)]
    [InlineData("Comfortable", GridDensity.Comfortable)]
    [InlineData("Tiny", GridDensity.Comfortable)]
    [InlineData(null, GridDensity.Comfortable)]
    public void ASavedDensity_ReadsBack_ComfortableWhenUnknown(string? raw, GridDensity density) =>
        Assert.Equal(density, GridDensityResources.Parse(raw));

    [Fact]
    public void TheDensity_SurvivesSaveAndLoad_AndIsInTheActivityLog()
    {
        Assert.Equal(GridDensity.Comfortable, Settings().Load().GridDensity);
        var settings = Settings();
        var logged = new List<ActivityEntry>();
        settings.Changed += logged.AddRange;

        settings.SetGridDensity(GridDensity.Compact);

        Assert.Equal(GridDensity.Compact, Settings().Load().GridDensity);
        var row = Assert.Single(logged);
        Assert.Equal(("Grid density", "Comfortable", "Compact", ActivitySources.Settings), (row.Setting, row.OldValue, row.NewValue, row.Source));
    }

    [Fact]
    public void TheThemes_DefineTheComfortableSpacing_SoAGridLooksAsBefore()
    {
        foreach (var theme in new[] { "DarkTheme", "LightTheme" })
        {
            UiThread.Run(() =>
            {
                var resources = (ResourceDictionary)Application.LoadComponent(new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative));
                Assert.Equal(GridDensityResources.CellPaddingOf(GridDensity.Comfortable), resources[GridDensityResources.CellPadding]);
                Assert.Equal(GridDensityResources.HeaderPaddingOf(GridDensity.Comfortable), resources[GridDensityResources.HeaderPadding]);
                Assert.Equal(12.0, resources[GridDensityResources.FontSize]);
            });
        }
    }

    private static ActiveWait Wait(int session, string program) =>
        new(session, 0, "LCK_M_X", 1_000, 1, null, "KEY: 5:72057594045726720", "suspended", "SELECT", 120, 1_400, 3_210, 0, 0, 0, 1,
            "svc_orders", "APP-WEB03", program, "SalesDb", "Lock", 0, "SELECT o.OrderId FROM dbo.Orders o", DateTime.Now, "Warning");

    // A page with a grid, in a window off screen as in the app; its rows' height in each density.
    [Fact]
    public void EveryGrid_TakesTheDensity_AtOnce_AndKeepsOneTextBlockPerCell() => UiThread.Run(() =>
    {
        var resources = Application.Current.Resources;
        var page = Wave2Pages.Processes(new FakeRepository
        {
            GetProcessesAsync   = () => Task.FromResult<IEnumerable<ProcessNode>>([]),
            GetActiveWaitsAsync = () => Task.FromResult<IEnumerable<ActiveWait>>(
                Enumerable.Range(0, 30).Select(i => Wait(60 + i, i % 2 == 0 ? "OrderService" : "ReportRunner")).ToList()),
        }, Settings(), view: nameof(ProcessesView.Waiting));
        var window = new Window { Content = page, Width = 1100, Height = 700, Left = -20_000, Top = -20_000,
                                  WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            UiThread.Wait(page.ViewModel.RefreshAsync());
            var comfortable = RowHeight(page, window, "comfortable.png");

            GridDensityResources.Apply(resources, GridDensity.Compact);
            var compact = RowHeight(page, window, "compact.png");

            Assert.True(comfortable - compact >= 6, $"comfortable {comfortable}, compact {compact}");
            // The cells are as they were: one TextBlock each, the same automation.
            var cell = UiThread.Find<DataGridCell>(page.WaitingGrid).First();
            Assert.Single(UiThread.Find<TextBlock>(cell));
            Assert.Equal(GridDensityResources.CellPaddingOf(GridDensity.Compact), cell.Padding);
        }
        finally
        {
            // The test thread's application is shared: back to the comfortable spacing.
            GridDensityResources.Apply(resources, GridDensity.Comfortable);
            window.Close();
        }
    });

    private static double RowHeight(Page page, Window window, string name)
    {
        UiThread.Wait(Task.Delay(100));
        window.UpdateLayout();
        Directory.CreateDirectory(RenderDir);
        var bitmap = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(RenderDir, name)))
            encoder.Save(file);
        return UiThread.Find<DataGridRow>(page).First().ActualHeight;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
