using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Config Checks (#42, #186): the server's configuration against best practice. MAXDOP, cost
/// threshold for parallelism, max server memory, TempDB's data files, and each database's
/// auto-shrink, page verify and compatibility level. Each check is Pass or Warn, with the value
/// now, the value recommended and why; a warning comes with the T-SQL to fix it, which is never
/// run here. The verdict counts the checks that fail, the riskiest first (#150).
/// </summary>
/// <param name="serverName">The connection's name, in the fix script's header.</param>
public sealed partial class ConfigChecksViewModel(IConfigurationRepository repo, IDialogService dialogs, string? serverName, TimeProvider? time = null)
    : PageViewModel
{
    /// <summary>The filter the verdict's Show it picks when the one chosen hides the check.</summary>
    internal const int WarningsFilter = 1;

    private static readonly (string Label, Func<ConfigCheck, bool> Keep, string Empty)[] Filters =
    [
        ("All checks",         _ => true,
            "There are no checks to show."),
        ("Warnings",           c => c.IsWarning,
            "Every setting checked is what best practice recommends."),
        ("Server settings",    c => !c.IsDatabaseCheck,
            "There are no server settings to show."),
        ("Database settings",  c => c.IsDatabaseCheck,
            "There are no database settings to show."),
        ("Not checked",        c => c.Status == ConfigCheckStatus.NotChecked,
            "Every check could be made."),
    ];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly string _serverName = string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName;

    // What was read last; changing the filter only filters it again.
    private ConfigCheckList? _checks;

    /// <summary>Which checks to list: the combo's choices.</summary>
    public IReadOnlyList<string> FilterNames { get; } = Filters.Select(f => f.Label).ToList();

    [ObservableProperty]
    private int _filterIndex;

    partial void OnFilterIndexChanged(int value) => Show();

    /// <summary>The checks the filter keeps, warnings first.</summary>
    public GridRows<ConfigCheck> Checks { get; } = [];

    /// <summary>The check in the drawer; kept across a refresh by its kind and scope, else the first.</summary>
    [ObservableProperty]
    private ConfigCheck? _selectedCheck;

    /// <summary>"12 results", "3 of 12 results".</summary>
    [ObservableProperty]
    private string _countText = "Checks";

    /// <summary>Something that kept some checks from being made, though the rest were read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProblem))]
    private string? _problem;

    public bool ShowsProblem => Problem is not null;

    /// <summary>When it was read, and that nothing was changed; "Reading…" the first time.</summary>
    [ObservableProperty]
    private string _readNote = string.Empty;

    /// <summary>Why no check is listed: none matches the filter.</summary>
    [ObservableProperty]
    private string? _noDataText;

    [ObservableProperty]
    private bool _showsNoData;

    /// <summary>A check to show: the verdict's Show it selected it. The view scrolls to it and opens the drawer.</summary>
    public event Action<ConfigCheck>? ShowRow;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        if (_checks is null)
            ReadNote = "Reading the server's configuration…";
        _checks = await repo.GetConfigurationChecksAsync(cancel);
        Verdict = ConfigChecksVerdict.From(_checks);
        Show();
    }

    // Nothing read yet: the verdict says why. Later, the results read last stay shown, and the top bar says it failed.
    protected override void ShowLoadError(Exception exception)
    {
        if (_checks is not null)
            return;
        ReadNote = string.Empty;
        ShowError("the server's configuration", exception);
    }

    private void Show()
    {
        if (_checks is not { } list)
            return;

        var filter = Filters[Math.Clamp(FilterIndex, 0, Filters.Length - 1)];
        var shown  = list.Checks.Where(filter.Keep).ToList();

        // Keep the same check across a refresh; otherwise the first, a warning if there is one.
        var selected = SelectedCheck;
        Checks.Replace(shown);
        SelectedCheck = shown.FirstOrDefault(c => selected is not null && c.Kind == selected.Kind && c.Scope == selected.Scope)
                        ?? shown.FirstOrDefault();

        CountText = shown.Count == list.Checks.Count
            ? $"{list.Checks.Count:N0} result{(list.Checks.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Checks.Count:N0} results";
        Problem     = list.Problem;
        ReadNote    = $"Checked at {_time.GetLocalNow():HH:mm:ss}. Settings are read as the server is using them now; nothing is changed.";
        NoDataText  = filter.Empty;
        ShowsNoData = shown.Count == 0;
        ScriptCommand.NotifyCanExecuteChanged();
    }

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ConfigChecksVerdict.ShowCheck: ShowSubject(); break;
            case ConfigChecksVerdict.Script:    Script();      break;
        }
    }

    // The verdict's Show it: the check it names, selected (with the warnings listed if the filter
    // hides it), in view and in the drawer.
    private void ShowSubject()
    {
        if (_checks is null || ConfigChecksVerdict.Riskiest(_checks) is not { } subject)
            return;
        if (!Filters[Math.Clamp(FilterIndex, 0, Filters.Length - 1)].Keep(subject))
            FilterIndex = Filters[WarningsFilter].Keep(subject) ? WarningsFilter : 0;
        SelectedCheck = subject;
        ShowRow?.Invoke(subject);
    }

    /// <summary>One T-SQL script with the change for every warning, to review and run yourself: from the button or the verdict.</summary>
    [RelayCommand(CanExecute = nameof(CanScript))]
    private void Script()
    {
        var now = _time.GetLocalNow().DateTime;
        if (_checks?.FixScript(_serverName, now) is not { } script)
            return;

        var fixes = _checks.Checks.Count(c => c.IsWarning && c.FixScript is not null);
        dialogs.ShowSqlScript(new SqlScript(
            "Configuration Fixes",
            $"Review before running. {fixes:N0} change{(fixes == 1 ? "" : "s")}, one per warning. Server settings take effect at " +
            "RECONFIGURE; a compatibility level change can change query plans, so turn Query Store on first. " +
            "SqlVitals does not run this script.",
            script,
            $"ConfigurationFixes_{now:yyyyMMdd_HHmm}.sql"), modal: true);
    }

    private bool CanScript() => _checks?.Checks.Any(c => c.IsWarning && c.FixScript is not null) == true;
}
