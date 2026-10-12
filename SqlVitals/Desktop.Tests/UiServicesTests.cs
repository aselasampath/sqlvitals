using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Services.Wpf;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The services view models ask for instead of WPF (#182): each comes from the composition root,
/// and the WPF ones behave as the code they replaced did.
/// </summary>
public sealed class UiServicesTests
{
    // ── The composition root ─────────────────────────────────────────────

    public static TheoryData<Type, Type> Registrations => new()
    {
        { typeof(IDialogService), typeof(WpfDialogService) },
        { typeof(IClipboard),     typeof(WpfClipboard) },
        { typeof(IFileDialogs),   typeof(WpfFileDialogs) },
        { typeof(INavigator),     typeof(ShellNavigator) },
        { typeof(IThemeService),  typeof(WpfThemeService) },
        { typeof(IUiDispatcher),  typeof(WpfUiDispatcher) },
    };

    [Theory]
    [MemberData(nameof(Registrations))]
    public void CompositionRoot_ResolvesEachService_OnceForTheApp(Type service, Type implementation)
    {
        var services = CompositionRoot.Build();

        var first = services.GetRequiredService(service);
        Assert.IsType(implementation, first);
        Assert.Same(first, services.GetRequiredService(service));
    }

    [Fact]
    public void CompositionRoot_GetsFromTheAppsServices() =>
        Assert.Same(CompositionRoot.Services.GetRequiredService<IThemeService>(), CompositionRoot.Get<IThemeService>());

    // ── The navigator ────────────────────────────────────────────────────

    [Fact]
    public async Task ShellNavigator_WithoutAWindow_DoesNothing()
    {
        var navigator = new ShellNavigator(() => null);

        await navigator.GoToAsync("Waits");
        // A page merged into another (#178) opens that one.
        await navigator.GoToAsync("ActiveWaits");
        await navigator.GoToAsync("QueryStore", new FocusFilter(FocusKind.Query, 42), "Query Regressions");
        await navigator.BackAsync();
        await navigator.ForwardAsync();
    }

    [Fact]
    public async Task ShellNavigator_RefusesATagNoPageHas()
    {
        // The window would open Live Metrics instead, which would hide the mistake.
        var navigator = new ShellNavigator(() => throw new InvalidOperationException("never asked for the window"));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => navigator.GoToAsync("WaitsTrend"));
        Assert.Contains("\"WaitsTrend\"", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => navigator.GoToAsync("Sessions", new FocusFilter(FocusKind.Session, 1), "Processes"));
    }

    [Fact]
    public void VerdictsOpen_OnlyPagesTheCatalogHas()
    {
        var tags = new[] { "CPU", "I/O", "Memory", "Locks" }.Select(TopWaitsVerdict.CausePage).OfType<(string Tag, string Text)>().Select(p => p.Tag)
            .Concat(PerfmonVerdict.Limits.Select(l => l.Page))
            .Concat(["Processes", "Waits", "QueryRegressions", "QueryStore"]);

        Assert.All(tags, tag => Assert.Contains(tag, PageCatalog.Types.Keys));
    }

    // ── The theme ────────────────────────────────────────────────────────

    [Fact]
    public void Theme_SwapsTheDictionary_AndSaysSo() => UiThread.Run(() =>
    {
        // Its own dictionary, as the application's would be: the page tests share the application, in the light theme.
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/SqlVitals.Desktop;component/Styles/LightTheme.xaml"),
        });
        var theme   = new WpfThemeService(() => resources);
        var changes = 0;
        theme.Changed += (_, _) => changes++;

        theme.Set(AppTheme.Dark);
        Assert.Equal(AppTheme.Dark, theme.Current);
        Assert.Equal(1, changes);
        Assert.EndsWith("DarkTheme.xaml", Assert.Single(resources.MergedDictionaries).Source.OriginalString);
        Assert.Equal("#FF0F0F1A", resources["BgDeepColor"].ToString());

        theme.Set(AppTheme.Dark);
        Assert.Equal(1, changes);

        theme.Set(AppTheme.Light);
        Assert.Equal(2, changes);
        Assert.Equal("#FFF0F2F5", resources["BgDeepColor"].ToString());
    });

    [Fact]
    public void Theme_WithoutAnApplication_StaysAsItIs()
    {
        var theme = new WpfThemeService(() => null);
        theme.Changed += (_, _) => Assert.Fail("nothing changed");

        theme.Set(AppTheme.Dark);

        Assert.Equal(AppTheme.Light, theme.Current);
    }

    // ── The UI thread ────────────────────────────────────────────────────

    [Fact]
    public async Task UiDispatcher_RunsOnItsThread()
    {
        WpfUiDispatcher? ui = null;
        Thread? uiThread = null;
        UiThread.Run(() => (ui, uiThread) = (new WpfUiDispatcher(Dispatcher.CurrentDispatcher), Thread.CurrentThread));

        Assert.False(ui!.IsOnUiThread);

        Thread? ranOn = null;
        await ui.InvokeAsync(() => ranOn = Thread.CurrentThread);
        Assert.Same(uiThread, ranOn);

        var posted = new TaskCompletionSource<Thread>();
        ui.Post(() => posted.SetResult(Thread.CurrentThread));
        Assert.Same(uiThread, await posted.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // On the UI thread already: at once, not queued behind what is waiting.
        UiThread.Run(() =>
        {
            Assert.True(ui.IsOnUiThread);
            var now = false;
            Assert.True(ui.InvokeAsync(() => now = true).IsCompleted);
            Assert.True(now);
        });
    }

    // ── The error line a verdict shows ───────────────────────────────────

    [Fact]
    public void ErrorStatus_TagsARepositoryError_AndHidesSecrets()
    {
        var inner = new InvalidOperationException("Login failed; Password=hunter2");
        Assert.Equal("Error [WaitStatsRepository.GetTopWaitTypesAsync]: " + ConnectionSettingsService.RedactSecrets(inner.Message),
                     ErrorStatus.Format(new WaitStatsException("WaitStatsRepository", "GetTopWaitTypesAsync", inner)));
        Assert.DoesNotContain("hunter2", ErrorStatus.Format(new WaitStatsException("WaitStatsRepository", "GetTopWaitTypesAsync", inner)));
        Assert.Equal("Error: Timeout", ErrorStatus.Format(new TimeoutException("Timeout")));
    }
}
