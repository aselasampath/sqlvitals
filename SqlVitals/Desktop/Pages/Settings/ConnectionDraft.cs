using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Everything on a connection's page (#131), on every tab, as typed. Two are equal when nothing
/// differs, so typing a value and putting it back leaves no unsaved changes.
/// </summary>
/// <param name="Thresholds">Every threshold box's text while it uses its own thresholds; empty otherwise.</param>
internal sealed record ConnectionDraft(
    string      Name,
    string      Server,
    SqlAuthMode Authentication,
    string      UserName,
    string      Password,
    bool        SavePassword,
    string      Database,
    EncryptMode Encrypt,
    bool        TrustServerCertificate,
    string      ConnectTimeout,
    string      AdditionalParameters,
    bool        MonitorInBackground,
    bool        Notifies,
    bool        OwnThresholds,
    string      Thresholds)
{
    /// <summary>
    /// Whether saving has to sign in to the server first: anything that changes how SqlVitals
    /// connects. A name, the monitoring and alert settings, or thresholds save without it, so a
    /// server that is down can still be muted or renamed.
    /// </summary>
    public bool NeedsServerCheck(ConnectionDraft saved) =>
        Server != saved.Server ||
        Authentication != saved.Authentication ||
        UserName != saved.UserName ||
        Password != saved.Password ||
        Database != saved.Database ||
        Encrypt != saved.Encrypt ||
        TrustServerCertificate != saved.TrustServerCertificate ||
        ConnectTimeout != saved.ConnectTimeout ||
        AdditionalParameters != saved.AdditionalParameters;

    /// <summary>The tabs with something unsaved on them.</summary>
    public IReadOnlyList<ConnectionTab> ChangedTabs(ConnectionDraft saved)
    {
        var tabs = new List<ConnectionTab>();
        if (Name != saved.Name || Server != saved.Server || Authentication != saved.Authentication || UserName != saved.UserName ||
            Password != saved.Password || SavePassword != saved.SavePassword || Database != saved.Database ||
            Encrypt != saved.Encrypt || TrustServerCertificate != saved.TrustServerCertificate)
            tabs.Add(ConnectionTab.Connection);
        if (MonitorInBackground != saved.MonitorInBackground || Notifies != saved.Notifies)
            tabs.Add(ConnectionTab.Monitoring);
        if (OwnThresholds != saved.OwnThresholds || Thresholds != saved.Thresholds)
            tabs.Add(ConnectionTab.Thresholds);
        if (ConnectTimeout != saved.ConnectTimeout || AdditionalParameters != saved.AdditionalParameters)
            tabs.Add(ConnectionTab.Advanced);
        return tabs;
    }
}
