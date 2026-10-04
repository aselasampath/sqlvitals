using System.IO;
using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// "Since you last looked" (#143): when each server's Overview was last opened, kept in the
/// settings file, on a fake clock.
/// </summary>
public sealed class OverviewVisitsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ConnectionSettingsService _settings;
    private readonly ConnectionSettings _prod = new() { Server = "prod", Name = "PROD" };
    private readonly ConnectionSettings _uat  = new() { Server = "uat",  Name = "UAT" };

    public OverviewVisitsTests()
    {
        _settings = new ConnectionSettingsService(_dir);
        _settings.Upsert(_prod);
        _settings.Upsert(_uat);
    }

    private OverviewVisits Visits() => new(new ConnectionSettingsService(_dir), _clock);

    [Fact]
    public void TheFirstLook_CoversTheLastDay()
    {
        var look = Visits().Open(_prod.Id);

        Assert.True(look.IsFirstLook);
        Assert.Equal((Start.UtcDateTime.AddHours(-24), Start.UtcDateTime), (look.FromUtc, look.ToUtc));
    }

    [Fact]
    public void ComingBackSoon_CarriesOnTheSameLook()
    {
        // Overview, then Deadlocks from it, then Back: the deadlocks are still counted.
        var visits = Visits();
        _clock.Advance(TimeSpan.FromHours(2));
        visits.Open(_prod.Id);   // first look: the last day
        var first = visits.Open(_prod.Id);
        _clock.Advance(TimeSpan.FromMinutes(10));

        var back = visits.Open(_prod.Id);

        Assert.True(back.IsFirstLook);
        Assert.Equal((first.FromUtc, _clock.GetUtcNow().UtcDateTime), (back.FromUtc, back.ToUtc));

        // Each return carries it on for another quarter of an hour; after that, a new look.
        _clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(first.FromUtc, visits.Open(_prod.Id).FromUtc);
        var openedAt = _clock.GetUtcNow().UtcDateTime;
        _clock.Advance(TimeSpan.FromMinutes(15));
        var next = visits.Open(_prod.Id);
        Assert.Equal((openedAt, openedAt), (next.FromUtc, next.LastLookedUtc));
    }

    [Fact]
    public void TheNextLook_RunsFromTheOneBefore_AcrossARestart()
    {
        Visits().Open(_prod.Id);
        _clock.Advance(TimeSpan.FromHours(3));

        // A new service: the time was saved, not kept in memory.
        var look = Visits().Open(_prod.Id);

        Assert.False(look.IsFirstLook);
        Assert.Equal(Start.UtcDateTime, look.LastLookedUtc);
        Assert.Equal((Start.UtcDateTime, Start.UtcDateTime.AddHours(3)), (look.FromUtc, look.ToUtc));
        Assert.Equal(DateTimeKind.Utc, look.FromUtc.Kind);
    }

    [Fact]
    public void EachServer_HasItsOwnLastLook()
    {
        var visits = Visits();
        visits.Open(_prod.Id);
        _clock.Advance(TimeSpan.FromMinutes(30));
        visits.Open(_uat.Id);
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(Start.UtcDateTime, visits.Open(_prod.Id).LastLookedUtc);
        Assert.Equal(Start.UtcDateTime.AddMinutes(30), visits.Open(_uat.Id).LastLookedUtc);
    }

    [Fact]
    public void ALookLongAgo_IsCappedAtAWeek()
    {
        Visits().Open(_prod.Id);
        _clock.Advance(TimeSpan.FromDays(30));

        var look = Visits().Open(_prod.Id);

        Assert.True(look.Capped);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(-7), look.FromUtc);
    }

    [Fact]
    public void Looking_IsNotInTheActivityLog_AndARemovedServerIsForgotten()
    {
        var raised = new List<ActivityEntry>();
        var service = new ConnectionSettingsService(_dir);
        service.Changed += raised.AddRange;
        var visits = new OverviewVisits(service, _clock);

        visits.Open(_prod.Id);
        visits.Open(_uat.Id);
        Assert.Empty(raised);
        Assert.Equal(2, service.Load().OverviewOpenedUtc.Count);

        service.Remove(_uat.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));
        visits.Open(_prod.Id);

        Assert.Equal([_prod.Id], service.Load().OverviewOpenedUtc.Keys);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
