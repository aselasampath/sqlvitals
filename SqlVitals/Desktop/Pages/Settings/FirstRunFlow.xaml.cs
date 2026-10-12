using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>The first-run flow's steps (#177).</summary>
public enum FirstRunStep
{
    Connect,
    Check,
    Start,
}

/// <summary>
/// First run (#177): with no saved connection, Settings opens on this instead of an empty list.
/// Connect is the connection editor; Check signs in and lists the permissions the login lacks,
/// with the GRANT; Start turns monitoring on (by default) and opens the server's Overview. Skip
/// leaves it for Settings → Connections at any step.
/// </summary>
internal partial class FirstRunFlow : UserControl
{
    private readonly SettingsContext _context;

    // The connection saved in Connect; null before.
    private Guid?  _id;
    private string _name = string.Empty;
    private bool   _closed;

    public FirstRunFlow(SettingsContext context)
    {
        _context = context;
        // Bound with ElementName before InitializeComponent: none of them change.
        SkipCommand   = new RelayCommand(() => _ = SkipAsync());
        NextCommand   = new RelayCommand(() => Show(FirstRunStep.Start));
        FinishCommand = new RelayCommand(() => _ = FinishAsync());
        InitializeComponent();

        Editor = new ConnectionEditor(context, connectionId: null);
        Editor.UseForFirstRun();
        Editor.ConnectionSaved += Connected;
        ConnectStep.Content = Editor;
        Show(FirstRunStep.Connect);
    }

    /// <summary>Skipped, or finished: Settings shows Connections again.</summary>
    public event Action? Closed;

    public ICommand SkipCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand FinishCommand { get; }

    public FirstRunStep Step { get; private set; }

    /// <summary>Step 1: the new connection's page.</summary>
    public ConnectionEditor Editor { get; }

    /// <summary>Step 2's check, once a connection is saved.</summary>
    public PermissionCheckView? Check { get; private set; }

    /// <summary>The connection saved in step 1; null before.</summary>
    public Guid? ConnectionId => _id;

    /// <summary>Whether the flow can be left: in Connect, what's typed asks to be saved or discarded first.</summary>
    public Task<bool> TryLeaveAsync() => Step == FirstRunStep.Connect ? Editor.TryCloseAsync() : Task.FromResult(true);

    // ── Steps ─────────────────────────────────────────────────────────────

    // Saved and picked up by the app (the first connection saved is the active one): check it.
    internal void Connected(Guid id)
    {
        if (_closed)
            return;
        _id   = id;
        _name = _context.Service.Load().Connections.FirstOrDefault(c => c.Id == id)?.DisplayName ?? "the server";

        if (_context.CheckPermissions is { } check && _context.Service.Load().Connections.FirstOrDefault(c => c.Id == id) is { } saved)
        {
            var model = new PermissionCheckViewModel(token => check(saved, token), _context.Clipboard, _name);
            Check = new PermissionCheckView(model);
            CheckHost.Content = Check;
            TxtCheckTitle.Text = $"Check what {_name}'s login may read";
            Show(FirstRunStep.Check);
            _ = model.CheckAsync();   // says its own errors
        }
        else
        {
            Show(FirstRunStep.Start);
        }
    }

    internal void Show(FirstRunStep step)
    {
        Step = step;
        ConnectStep.Visibility = step == FirstRunStep.Connect ? Visibility.Visible : Visibility.Collapsed;
        CheckStep.Visibility   = step == FirstRunStep.Check ? Visibility.Visible : Visibility.Collapsed;
        StartStep.Visibility   = step == FirstRunStep.Start ? Visibility.Visible : Visibility.Collapsed;

        TxtStartTitle.Text = $"Start watching {_name}";
        BtnFinish.Content  = $"Open {_name}'s Overview  ›";
        AutomationProperties.SetName(BtnFinish, $"Open {_name}'s Overview");

        ShowStep(Dot1, Num1, Name1, FirstRunStep.Connect, step);
        ShowStep(Dot2, Num2, Name2, FirstRunStep.Check, step);
        ShowStep(Dot3, Num3, Name3, FirstRunStep.Start, step);
        AutomationProperties.SetName(Steps, $"Step {(int)step + 1} of 3: {step}");
    }

    // Done: ✓ on the accent; the current one: its number on the accent; still to come: muted.
    private static void ShowStep(Border dot, TextBlock number, TextBlock name, FirstRunStep which, FirstRunStep current)
    {
        var done = which < current;
        var now  = which == current;
        number.Text = done ? "✓" : ((int)which + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        dot.SetResourceReference(Border.BackgroundProperty, done || now ? "Accent" : "BgCard");
        dot.SetResourceReference(Border.BorderBrushProperty, done || now ? "Accent" : "Border");
        if (done || now)
            number.ClearValue(TextBlock.ForegroundProperty);   // the style's white
        else
            number.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        name.SetResourceReference(TextBlock.ForegroundProperty, now ? "TextPrimary" : "TextMuted");
        name.FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal;
    }

    // ── Skip and finish ───────────────────────────────────────────────────

    [ReportsItsErrors]
    internal async Task SkipAsync()
    {
        try
        {
            if (!await TryLeaveAsync())
                return;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("First run: could not skip", ex);
        }
    }

    /// <summary>Start monitoring now, if ticked, then the server's Overview.</summary>
    [ReportsItsErrors]
    internal async Task FinishAsync()
    {
        if (_id is not { } id)
            return;
        try
        {
            if (ChkStartMonitoring.IsChecked == true)
                await _context.Starter.StartAsync(id, ActivitySources.FirstRun);
        }
        catch (Exception ex)
        {
            AppLog.Error("First run: could not start monitoring", ex);
        }
        Close();
        _context.OpenServer(id);
    }

    private void Close()
    {
        if (_closed)
            return;
        _closed = true;
        Check?.Model.Cancel();
        Closed?.Invoke();
    }
}
