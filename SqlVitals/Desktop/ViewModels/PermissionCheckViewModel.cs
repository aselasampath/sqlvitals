using System.ComponentModel;
using System.Windows.Input;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Permissions;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// The permission check (#177), for the first-run flow and a connection's card in Settings: signs
/// in, reads which permissions the login holds, lists what each missing one costs and offers the
/// GRANT script that adds them. Asks the server only when <see cref="CheckCommand"/> runs.
/// </summary>
public sealed class PermissionCheckViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<CancellationToken, Task<PermissionProbe>> _check;
    private readonly IClipboard _clipboard;
    private CancellationTokenSource? _running;

    /// <param name="check">Signs in and reads the login's permissions; throws when it can't.</param>
    /// <param name="connectionName">The connection's name, as the cards show it.</param>
    public PermissionCheckViewModel(Func<CancellationToken, Task<PermissionProbe>> check, IClipboard clipboard, string connectionName)
    {
        _check         = check;
        _clipboard     = clipboard;
        ConnectionName = connectionName;
        CheckCommand   = new RelayCommand(() => _ = CheckAsync());
        CopyCommand    = new RelayCommand(Copy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ConnectionName { get; }

    public PermissionCheckState State { get; private set; } = PermissionCheckState.NotRun;

    public bool IsChecking => State == PermissionCheckState.Checking;

    /// <summary>"Checking…", the report's summary, or why it couldn't check.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>Null until a check has read the permissions.</summary>
    public PermissionReport? Report { get; private set; }

    public IReadOnlyList<PermissionRow> Rows { get; private set; } = [];

    /// <summary>The GRANT script for what's missing; empty when nothing is.</summary>
    public string Script { get; private set; } = string.Empty;

    public bool HasScript => Script.Length > 0;

    /// <summary>Who the script grants to, and where to run it.</summary>
    public string ScriptIntro => Report?.Probe is not { } probe
        ? string.Empty
        : probe.Azure == Engine.Repositories.AzureKind.SqlDatabase
            ? $"Ask an admin of {probe.Database} to run this script, then check again:"
            : $"Ask a DBA to run this script for {probe.Login}, then check again. It only grants reads:";

    /// <summary>"Copied" for a moment after Copy script.</summary>
    public string CopyText { get; private set; } = "Copy script";

    public ICommand CheckCommand { get; }

    public ICommand CopyCommand { get; }

    /// <summary>"Check" before the first run, "Check again" after.</summary>
    public string CheckText => State == PermissionCheckState.NotRun ? "Check permissions" : "Check again";

    /// <summary>Signs in and reads the permissions. Never throws: a failure is said in <see cref="Status"/>.</summary>
    [ReportsItsErrors]
    public async Task CheckAsync()
    {
        if (IsChecking)
            return;
        _running?.Dispose();
        _running = new CancellationTokenSource();
        var token = _running.Token;

        Set(PermissionCheckState.Checking, $"Signing in to {ConnectionName} and reading the login's permissions…");
        try
        {
            var report = PermissionCheck.Evaluate(await _check(token));
            Report = report;
            Rows   = report.Results.Select(r => new PermissionRow(r)).ToList();
            Script = report.GrantScript ?? string.Empty;
            Set(PermissionCheckState.Done, report.Summary);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Set(PermissionCheckState.NotRun, string.Empty);
        }
        catch (Exception ex)
        {
            Report = null;
            Rows   = [];
            Script = string.Empty;
            // The server's own words, without the query's tag: "Login failed for user 'app'."
            var message = ex is WaitStatsException { InnerException: { } inner } ? inner.Message : ex.Message;
            Set(PermissionCheckState.Failed, $"Couldn't check: {ConnectionSettingsService.RedactSecrets(message)}");
        }
    }

    /// <summary>Stops a check still running: its page was left.</summary>
    public void Cancel() => _running?.Cancel();

    public void Dispose()
    {
        _running?.Cancel();
        _running?.Dispose();
        _running = null;
    }

    private void Copy()
    {
        if (!HasScript)
            return;
        _clipboard.SetText(Script);
        CopyText = "✓  Copied";
        Changed(nameof(CopyText));
    }

    private void Set(PermissionCheckState state, string status)
    {
        State    = state;
        Status   = status;
        CopyText = "Copy script";
        Changed(string.Empty);
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
