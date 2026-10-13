using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// App Connections (#185): the user connections grouped by application, their counts as cards, the
/// applications holding the most charted, and the verdict on who holds them (#149). It has no actions.
/// </summary>
public sealed partial class ApplicationConnectionsViewModel(ISessionRepository repo) : PageViewModel
{
    /// <summary>How many applications the chart shows, the most connections first.</summary>
    internal const int Charted = 15;

    /// <summary>The applications, as read.</summary>
    public GridRows<ApplicationConnection> Applications { get; } = [];

    /// <summary>The grid's selected application; kept across a refresh by its name.</summary>
    [ObservableProperty]
    private ApplicationConnection? _selectedApplication;

    /// <summary>The counts: applications, connections, active requests and CPU; none when nothing is connected.</summary>
    [ObservableProperty]
    private IReadOnlyList<KpiCard> _counts = [];

    /// <summary>"5 application(s)  •  213 total connections" over the cards.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Folded, the counts say it in one line (#149).</summary>
    [ObservableProperty]
    private string? _countsSummary;

    /// <summary>Folded, the chart says it in one line (#149).</summary>
    [ObservableProperty]
    private string? _chartSummary;

    /// <summary>The applications the chart shows, the most connections first; the view draws them.</summary>
    [ObservableProperty]
    private IReadOnlyList<ApplicationConnection> _chartApplications = [];

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        var apps = (await repo.GetApplicationConnectionsAsync(cancel)).ToList();
        Verdict = AppConnectionsVerdict.From(apps);

        var connections = apps.Sum(a => a.CurrentConnections);
        var mostFirst   = apps.OrderByDescending(a => a.CurrentConnections).ToList();
        Summary = apps.Count > 0
            ? $"{apps.Count} application(s)  •  {connections} total connections"
            : "No active user connections";
        CountsSummary = apps.Count == 0 ? "No active user connections"
            : $"{apps.Count:N0} application(s) · {connections:N0} connections · {apps.Sum(a => a.ActiveRequests):N0} active requests";
        ChartSummary = apps.Count == 0 ? "Nothing to chart"
            : string.Join(" · ", mostFirst.Take(3).Select(a => $"{AppConnectionsVerdict.Name(a)} {a.CurrentConnections:N0}"));

        Counts = apps.Count == 0 ? [] :
        [
            new("Applications",      $"{apps.Count}",                              KpiCard.Tones.Accent,  "Distinct applications connected (grouped by program_name)."),
            new("Total Connections", $"{connections:N0}",                          KpiCard.Tones.Info,    "Total user sessions currently connected."),
            new("Active Requests",   $"{apps.Sum(a => a.ActiveRequests):N0}",      KpiCard.Tones.Success, "Sessions with an actively executing request right now."),
            new("Total CPU (ms)",    $"{apps.Sum(a => a.TotalCpuTimeMs):N0}",      KpiCard.Tones.Warning, "Cumulative CPU time across all connected sessions."),
        ];
        ChartApplications = mostFirst.Take(Charted).ToList();

        var selected = SelectedApplication?.ApplicationName;
        Applications.Replace(apps);
        SelectedApplication = selected is null ? null : apps.FirstOrDefault(a => a.ApplicationName == selected);
    }

    protected override void ShowLoadError(Exception exception) => ShowError("the connections", exception);
}
