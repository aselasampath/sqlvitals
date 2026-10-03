using System.IO;
using System.Text.Json.Nodes;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The sidebar pinned, collapsed or hidden (#142): Ctrl+B's cycle, the pin, collapsing in a narrow
/// window and coming back, and the state saved for the next start but kept out of the activity log.
/// </summary>
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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
