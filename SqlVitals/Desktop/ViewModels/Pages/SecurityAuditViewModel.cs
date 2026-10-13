using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Security;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Security Audit (#43, #186): who is sysadmin (or holds CONTROL SERVER), whether the sa login is
/// enabled, databases marked TRUSTWORTHY, and orphaned users. Each finding is a Risk, something to
/// Review, or a Pass, with the risk explained; a risk comes with the T-SQL to remove it, which is
/// never run here. The verdict names the risks, the highest first (#150).
/// </summary>
/// <param name="serverName">The connection's name, in the fix script's header.</param>
public sealed partial class SecurityAuditViewModel(ISecurityAuditRepository repo, IDialogService dialogs, string? serverName, TimeProvider? time = null)
    : PageViewModel
{
    /// <summary>The filter the verdict's Show it picks when the one chosen hides the finding.</summary>
    internal const int RisksFilter = 1;

    private static readonly (string Label, Func<SecurityFinding, bool> Keep, string Empty)[] Filters =
    [
        ("All findings",       _ => true,
            "There are no findings to show."),
        ("Risks",              f => f.IsRisk,
            "Nothing audited widens the attack surface."),
        ("To review",          f => f.Status == SecurityFindingStatus.Review,
            "There is nothing to review."),
        ("sysadmin members",   f => f.Kind == SecurityCheckKind.SysadminMember,
            "There are no sysadmin members to show."),
        ("sa login",           f => f.Kind == SecurityCheckKind.SaLogin,
            "The sa login couldn't be read."),
        ("TRUSTWORTHY",        f => f.Kind == SecurityCheckKind.Trustworthy,
            "There are no databases to show."),
        ("Orphaned users",     f => f.Kind == SecurityCheckKind.OrphanedUser,
            "There are no orphaned users to show."),
        ("Not checked",        f => f.Status == SecurityFindingStatus.NotChecked,
            "Everything could be checked."),
    ];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly string _serverName = string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName;

    // What was read last; changing the filter only filters it again.
    private SecurityFindingList? _findings;

    /// <summary>Which findings to list: the combo's choices.</summary>
    public IReadOnlyList<string> FilterNames { get; } = Filters.Select(f => f.Label).ToList();

    [ObservableProperty]
    private int _filterIndex;

    partial void OnFilterIndexChanged(int value) => Show();

    /// <summary>The findings the filter keeps, risks first.</summary>
    public GridRows<SecurityFinding> Findings { get; } = [];

    /// <summary>The finding in the drawer; kept across a refresh by its kind, scope and subject, else the first.</summary>
    [ObservableProperty]
    private SecurityFinding? _selectedFinding;

    /// <summary>"9 findings", "2 of 9 findings".</summary>
    [ObservableProperty]
    private string _countText = "Findings";

    /// <summary>Something that kept some findings from being checked, though the rest were read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProblem))]
    private string? _problem;

    public bool ShowsProblem => Problem is not null;

    /// <summary>When it was audited, and that nothing was changed; "Reading…" the first time.</summary>
    [ObservableProperty]
    private string _readNote = string.Empty;

    /// <summary>Why no finding is listed: none matches the filter, or nothing could be read.</summary>
    [ObservableProperty]
    private string? _noDataText;

    [ObservableProperty]
    private bool _showsNoData;

    /// <summary>A finding to show: the verdict's Show it selected it. The view scrolls to it and opens the drawer.</summary>
    public event Action<SecurityFinding>? ShowRow;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        if (_findings is null)
            ReadNote = "Reading the logins, databases and users…";
        _findings = await repo.GetSecurityAuditAsync(cancel);
        Verdict = SecurityAuditVerdict.From(_findings);
        Show();
    }

    // Nothing read yet: the verdict says why. Later, the results read last stay shown, and the top bar says it failed.
    protected override void ShowLoadError(Exception exception)
    {
        if (_findings is not null)
            return;
        ReadNote = string.Empty;
        ShowError("the logins, databases and users", exception);
    }

    private void Show()
    {
        if (_findings is not { } list)
            return;

        var filter = Filters[Math.Clamp(FilterIndex, 0, Filters.Length - 1)];
        var shown  = list.Findings.Where(filter.Keep).ToList();

        // Keep the same finding across a refresh; otherwise the first, a risk if there is one.
        var selected = SelectedFinding;
        Findings.Replace(shown);
        SelectedFinding = shown.FirstOrDefault(f => selected is not null && f.Kind == selected.Kind &&
                                                    f.Scope == selected.Scope && f.Subject == selected.Subject)
                          ?? shown.FirstOrDefault();

        CountText = !list.Available ? "Findings"
            : shown.Count == list.Findings.Count ? $"{list.Findings.Count:N0} finding{(list.Findings.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Findings.Count:N0} findings";
        Problem     = list.Available ? list.Problem : null;
        ReadNote    = list.Available ? $"Audited at {_time.GetLocalNow():HH:mm:ss}. Read from the catalog views; nothing is changed." : string.Empty;
        NoDataText  = !list.Available ? list.Problem : filter.Empty;
        ShowsNoData = shown.Count == 0;
        ScriptCommand.NotifyCanExecuteChanged();
    }

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case SecurityAuditVerdict.ShowFinding: ShowSubject(); break;
            case SecurityAuditVerdict.Script:      Script();      break;
        }
    }

    // The verdict's Show it: the finding it names, selected (with the risks listed if the filter
    // hides it), in view and in the drawer.
    private void ShowSubject()
    {
        if (_findings is null || SecurityAuditVerdict.Subject(_findings) is not { } subject)
            return;
        if (!Filters[Math.Clamp(FilterIndex, 0, Filters.Length - 1)].Keep(subject))
            FilterIndex = Filters[RisksFilter].Keep(subject) ? RisksFilter : 0;
        SelectedFinding = subject;
        ShowRow?.Invoke(subject);
    }

    /// <summary>One T-SQL script that removes every risk, to review and run yourself: from the button or the verdict.</summary>
    [RelayCommand(CanExecute = nameof(CanScript))]
    private void Script()
    {
        var now = _time.GetLocalNow().DateTime;
        if (_findings?.FixScript(_serverName, now) is not { } script)
            return;

        var fixes = _findings.Findings.Count(f => f.IsRisk && f.FixScript is not null);
        dialogs.ShowSqlScript(new SqlScript(
            "Security Fixes",
            $"Review before running. {fixes:N0} change{(fixes == 1 ? "" : "s")}, one per risk. Removing access can stop an " +
            "application or a person from working, so check first who uses each login. SqlVitals does not run this script.",
            script,
            $"SecurityFixes_{now:yyyyMMdd_HHmm}.sql"), modal: true);
    }

    private bool CanScript() => _findings?.Findings.Any(f => f.IsRisk && f.FixScript is not null) == true;
}
