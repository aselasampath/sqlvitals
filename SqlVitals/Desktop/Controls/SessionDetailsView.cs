using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.ExecutionPlans;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A session's details in the drawer (#147), for Processes: who it is, what it waits on or blocks,
/// how long its transaction has been open, its last statement, its plan and a KILL script to
/// review. Kept while the same session is selected, so a refresh updates it without closing the
/// plan or the script.
/// </summary>
public sealed class SessionDetailsView : StackPanel
{
    private readonly Func<int, Task<SessionPlan>> _loadPlan;
    private readonly Func<int, Task> _openWaits;

    private readonly Grid    _rows      = DrawerParts.Rows();
    private readonly TextBox _statement = DrawerParts.Code(maxHeight: 220);
    private readonly Button  _showPlan;
    private readonly TextBlock _planNote = DrawerParts.Note();
    private readonly ExecutionPlanView _plan = new() { Height = 300, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button  _planWindow;
    private readonly Button  _showScript;
    private readonly TextBox _script    = DrawerParts.Code(maxHeight: 260);
    private readonly StackPanel _scriptButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

    private ProcessNode? _node;
    private IReadOnlyList<ProcessNode> _snapshot = [];
    private SessionPlan? _loadedPlan;
    private int _planVersion;

    /// <param name="loadPlan">Reads a session's plan from the server.</param>
    /// <param name="openWaits">Opens Processes' Waiting view filtered to a session (#145, #178).</param>
    public SessionDetailsView(Func<int, Task<SessionPlan>> loadPlan, Func<int, Task> openWaits)
    {
        _loadPlan  = loadPlan;
        _openWaits = openWaits;

        Children.Add(_rows);

        // The last statement.
        Children.Add(DrawerParts.Heading("Last statement"));
        Children.Add(_statement);
        var statementButtons = DrawerParts.Row(
            DrawerParts.Button("Copy SQL", () => ClipboardHelper.SetText(_statement.Text), "Copy the statement"),
            DrawerParts.Button("Its waits →", () => _node is { } n ? _openWaits(n.SessionId) : Task.CompletedTask,
                   "Show the Waiting view filtered to this session and the sessions it blocks"));
        Children.Add(statementButtons);

        // The plan: read when asked, shown here, and in a window for a second monitor.
        Children.Add(DrawerParts.Heading("Execution plan"));
        _showPlan   = DrawerParts.Button("Show the plan", ShowPlanAsync, "Read the plan of what it runs, or of its last statement");
        _planWindow = DrawerParts.Button("Open in window", OpenPlanWindow, "Open the plan in its own window, to keep it beside the grid or on another monitor");
        _planWindow.Visibility = Visibility.Collapsed;
        Children.Add(DrawerParts.Row(_showPlan, _planWindow));
        Children.Add(_planNote);
        Children.Add(_plan);

        // The KILL script: shown to review and copy, never run.
        Children.Add(DrawerParts.Heading("KILL script (review)"));
        _showScript = DrawerParts.Button("Show the script", ShowScript, "A KILL statement for this session, with what to check first. SqlVitals never runs it.");
        Children.Add(DrawerParts.Row(_showScript));
        Children.Add(_script);
        _script.Visibility = Visibility.Collapsed;
        _scriptButtons.Children.Add(DrawerParts.Button("Copy", () => ClipboardHelper.SetText(_script.Text), "Copy the script"));
        _scriptButtons.Children.Add(DrawerParts.Button("Open in window", OpenScriptWindow, "Open the script in its own window, to save it as a file"));
        Children.Add(_scriptButtons);
    }

    /// <summary>The session shown.</summary>
    public int SessionId => _node?.SessionId ?? 0;

    /// <summary>The detail rows as shown, "Label: value"; for tests.</summary>
    internal IReadOnlyList<string> RowTexts =>
        DrawerParts.Texts(_rows);

    internal string Statement => _statement.Text;

    internal string Script => _script.Text;

    internal Button ShowPlanButton => _showPlan;

    internal Button ShowScriptButton => _showScript;

    internal string PlanNote => _planNote.Text;

    /// <summary>Shows the session as the latest snapshot has it.</summary>
    public void Show(ProcessNode node, IReadOnlyList<ProcessNode> snapshot)
    {
        var another = _node?.SessionId != node.SessionId;
        _node     = node;
        _snapshot = snapshot;

        DrawerParts.Fill(_rows, SessionDetails.Rows(node, snapshot));

        var text = node.CommandText?.Trim();
        _statement.Text = string.IsNullOrEmpty(text) ? "(no statement: the session hasn't run one, or it is gone from the cache)" : text;

        if (another)
        {
            // Another session: its plan is read again when asked for.
            _planVersion++;
            _loadedPlan            = null;
            _plan.Visibility       = Visibility.Collapsed;
            _planWindow.Visibility = Visibility.Collapsed;
            _planNote.Text         = string.Empty;
            _planNote.Visibility   = Visibility.Collapsed;
            _showPlan.IsEnabled    = true;
            _showPlan.Content      = "Show the plan";
        }
        if (_script.Visibility == Visibility.Visible)
            _script.Text = SessionDetails.KillScript(node, snapshot);
    }

    private async Task ShowPlanAsync()
    {
        if (_node is not { } node)
            return;
        var version = ++_planVersion;
        _showPlan.IsEnabled = false;
        _showPlan.Content   = "Reading the plan…";
        try
        {
            var plan = await _loadPlan(node.SessionId);
            if (version != _planVersion)
                return;
            _loadedPlan = plan;
            _plan.Show(ShowplanParser.Parse(plan.PlanXml), "No plan is cached for it any more.");
            _plan.Visibility       = plan.PlanXml is null ? Visibility.Collapsed : Visibility.Visible;
            _planWindow.Visibility = plan.PlanXml is null ? Visibility.Collapsed : Visibility.Visible;
            _planNote.Text = plan.PlanXml is null
                ? "No plan is cached for it: it hasn't run a statement, or its plan has left the cache."
                : plan.IsRunning ? "The plan of what it is running now." : "The cached plan of its last statement: it isn't running anything now.";
            _planNote.Visibility = Visibility.Visible;
            _showPlan.Content    = "Read the plan again";
        }
        catch (Exception ex)
        {
            if (version != _planVersion)
                return;
            _planNote.Text       = "The plan couldn't be read: " + ErrorStatus.Format(ex);
            _planNote.Visibility = Visibility.Visible;
            _showPlan.Content    = "Try again";
        }
        finally
        {
            if (version == _planVersion)
                _showPlan.IsEnabled = true;
        }
    }

    private void OpenPlanWindow()
    {
        if (_loadedPlan?.PlanXml is not { } xml || _node is not { } node)
            return;
        CompositionRoot.Get<IDialogService>().ShowQueryPlan(xml, node.CommandText, $"Execution plan · session {node.SessionId}");
    }

    private void ShowScript()
    {
        if (_node is not { } node)
            return;
        var shown = _script.Visibility == Visibility.Visible;
        _script.Text              = SessionDetails.KillScript(node, _snapshot);
        _script.Visibility        = shown ? Visibility.Collapsed : Visibility.Visible;
        _scriptButtons.Visibility = _script.Visibility;
        _showScript.Content       = shown ? "Show the script" : "Hide the script";
    }

    private void OpenScriptWindow()
    {
        if (_node is not { } node)
            return;
        // Beside the drawer, not over it: the session stays in view while the script is read.
        CompositionRoot.Get<IDialogService>().ShowSqlScript(new SqlScript(
            $"KILL session {node.SessionId}",
            "Review before running. SqlVitals never runs it: KILL rolls back the session's open transaction, which can take as long as the work did.",
            SessionDetails.KillScript(node, _snapshot), $"kill-session-{node.SessionId}.sql"), modal: false);
    }
}
