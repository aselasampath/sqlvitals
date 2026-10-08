using Microsoft.Extensions.Logging;
using SqlVitals.Engine.Home;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// When each server's Overview was last opened (#143), kept in the settings file, so its "Since
/// you last looked" covers what happened in between. Coming back to an Overview soon after, from a
/// page it linked to or with Back, carries on the same look rather than starting an empty one.
/// </summary>
/// <param name="log">Where a failure to read or save is written; <see cref="AppLog"/>'s when null (tests).</param>
public sealed class OverviewVisits(ConnectionSettingsService settings, TimeProvider? time = null, ILogger<OverviewVisits>? log = null)
{
    private readonly ILogger _log = log ?? AppLog.For<OverviewVisits>();

    /// <summary>How soon an Overview opened again carries on the look before.</summary>
    public static readonly TimeSpan CarriesOn = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // This run's looks: each server's window and when its Overview was last opened.
    private readonly Dictionary<Guid, (LookWindow Window, DateTime OpenedUtc)> _looks = [];

    /// <summary>
    /// Records that the server's Overview is opened now, and returns the window it covers: the look
    /// before carried on to now if it was opened within <see cref="CarriesOn"/>; otherwise from
    /// when it was last opened, the last day the first time. A failure to save only means the next
    /// look starts earlier.
    /// </summary>
    public LookWindow Open(Guid connectionId)
    {
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        LookWindow window;
        if (_looks.TryGetValue(connectionId, out var look) && nowUtc >= look.OpenedUtc && nowUtc - look.OpenedUtc < CarriesOn)
            window = look.Window with { ToUtc = nowUtc };
        else
        {
            DateTime? last = null;
            try { last = settings.Load().OverviewOpenedUtc.TryGetValue(connectionId, out var opened) ? opened : null; }
            catch (Exception ex) { _log.Error("Overview: could not read when it was last opened", ex); }
            window = SinceLastLooked.Window(last, nowUtc);
        }

        _looks[connectionId] = (window, nowUtc);
        try { settings.SetOverviewOpened(connectionId, nowUtc); }
        catch (Exception ex) { _log.Error("Overview: could not save when it was opened", ex); }
        return window;
    }
}
