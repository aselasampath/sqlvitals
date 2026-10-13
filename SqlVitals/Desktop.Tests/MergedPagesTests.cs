using System.IO;
using System.Windows;
using System.Windows.Media;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The pages that took in others (#178), rendered in both themes: Processes with its Waiting view,
/// Waits (Top Waits and Wait Trend). Plan Cache Health with Implicit Conversions is in
/// <see cref="Wave1PagesTests"/>, bound to its view model (#185). To look at:
/// %TEMP%\SqlVitalsTests\page-template-rollout\&lt;page&gt;-&lt;theme&gt;.png.
/// </summary>
public sealed class MergedPagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Settings() => new(_dir);

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private static readonly DateTime Now = DateTime.Now;

    private static ActiveWait Wait(int spid, int blockedBy, long ms) =>
        new(spid, blockedBy, "LCK_M_X", ms, ms / 1000.0, null, "KEY: 7:72057594043039744", "suspended", "UPDATE", 1_240, ms + 40, 18_400, 12, 3, 0, 1,
            "svc_orders", "APP-WEB03", "OrderService", "SalesDb", "Locks", 1, "UPDATE dbo.Orders SET Status = @p0", Now, "Critical");

    private static TopWaitType Top(int rank, string type, string category, long ms) =>
        new(rank, type, category, ms / 7, ms, ms / 50, ms / 9, ms - ms / 9, 11, ms / 1000.0, 7, 10, rank * 10, Now, "#000", "");

    [Theory, MemberData(nameof(Themes))]
    public void ProcessesWaiting_Renders(string theme) => UiThread.Run(() =>
    {
        using var themed = Themed(theme);
        var page = new ProcessesPage(new FakeRepository
        {
            GetProcessesAsync   = () => Task.FromResult<IEnumerable<ProcessNode>>([]),
            GetActiveWaitsAsync = () => Task.FromResult<IEnumerable<ActiveWait>>([Wait(112, 87, 252_000), Wait(115, 87, 31_000)]),
        }, Settings(), view: nameof(ProcessesView.Waiting));

        TemplateRolloutTests.Hosted(page, page.RefreshAsync, $"processes-waiting-{theme}.png", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.WaitingGrid);
            Assert.Equal(Visibility.Collapsed, page.ProcessMap.Visibility);
            Assert.Equal(2, page.WaitingGrid.Items.Count);
            var checkedBorder = (System.Windows.Controls.Border)page.BtnWaiting.Template.FindName("Bd", page.BtnWaiting);
            Assert.Equal(Colour("Accent"), ((SolidColorBrush)checkedBorder.Background).Color);
        });
    });

    [Theory, MemberData(nameof(Themes))]
    public void Waits_Renders(string theme) => UiThread.Run(() =>
    {
        using var themed = Themed(theme);
        var page = new WaitsPage(new FakeRepository
        {
            GetTopWaitTypesAsync    = () => Task.FromResult<IEnumerable<TopWaitType>>([Top(1, "PAGEIOLATCH_SH", "I/O", 4_120_000), Top(2, "LCK_M_X", "Locks", 820_000)]),
            GetCumulativeWaitsAsync = () => Task.FromResult<IEnumerable<WaitStatCumulative>>([]),
        }, null, Settings());
        ((IPageContext)page).TakeBarTools();

        TemplateRolloutTests.Hosted(page, page.RefreshAsync, $"waits-{theme}.png", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.TrendChart, minDetail: 180);
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.WaitsGrid, minDetail: 150);
            Assert.Equal(2, page.WaitsGrid.Items.Count);
        });
    });

    private static Color Colour(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    // The app's theme swapped for one test, and put back after it.
    internal static IDisposable Themed(string theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var before = dictionaries.ToList();
        dictionaries.Clear();
        dictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative)));
        return new Restore(() =>
        {
            dictionaries.Clear();
            foreach (var dictionary in before)
                dictionaries.Add(dictionary);
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
