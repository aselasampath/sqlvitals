using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>
/// First run (#177): with no saved connection, Settings opens on this instead of an empty list.
/// Connect is the connection editor; Check signs in and lists the permissions the login lacks,
/// with the GRANT; Start turns monitoring on (by default) and opens the server's Overview. Skip
/// leaves it for Settings → Connections at any step.
/// </summary>
public sealed partial class FirstRunFlowViewModel : ObservableObject
{
    private readonly SettingsContext _context;
    private bool _closed;

    internal FirstRunFlowViewModel(SettingsContext context)
    {
        _context = context;
        Editor = new ConnectionEditorViewModel(context, connectionId: null);
        Editor.UseForFirstRun();
        Editor.ConnectionSaved += Connected;
        Steps = Enum.GetValues<FirstRunStep>().Select(s => new FirstRunStepItem(s)).ToList();
        Show(FirstRunStep.Connect);
    }

    /// <summary>Skipped, or finished: Settings shows Connections again.</summary>
    public event Action? Closed;

    /// <summary>Connect, Check and Start, across the top.</summary>
    public IReadOnlyList<FirstRunStepItem> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepsName), nameof(IsConnect), nameof(IsCheck), nameof(IsStart))]
    private FirstRunStep _step;

    public bool IsConnect => Step == FirstRunStep.Connect;
    public bool IsCheck   => Step == FirstRunStep.Check;
    public bool IsStart   => Step == FirstRunStep.Start;

    /// <summary>"Step 1 of 3: Connect", for a screen reader.</summary>
    public string StepsName => $"Step {(int)Step + 1} of 3: {Step}";

    /// <summary>Step 1: the new connection's page.</summary>
    public ConnectionEditorViewModel Editor { get; }

    /// <summary>Step 2's check, once a connection is saved.</summary>
    [ObservableProperty]
    private PermissionCheckViewModel? _check;

    /// <summary>The connection saved in step 1; null before.</summary>
    public Guid? ConnectionId { get; private set; }

    /// <summary>The connection's name, as step 2 and 3 say it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckTitle), nameof(StartTitle), nameof(FinishText), nameof(FinishName))]
    private string _name = string.Empty;

    public string CheckTitle => $"Check what {Name}'s login may read";
    public string StartTitle => $"Start watching {Name}";
    public string FinishText => $"Open {Name}'s Overview  ›";
    public string FinishName => $"Open {Name}'s Overview";

    /// <summary>Start monitoring now: on by default.</summary>
    [ObservableProperty]
    private bool _startMonitoring = true;

    /// <summary>Whether the flow can be left: in Connect, what's typed asks to be saved or discarded first.</summary>
    public Task<bool> TryLeaveAsync() => Step == FirstRunStep.Connect ? Editor.TryCloseAsync() : Task.FromResult(true);

    // ── Steps ─────────────────────────────────────────────────────────────

    // Saved and picked up by the app (the first connection saved is the active one): check it.
    internal void Connected(Guid id)
    {
        if (_closed)
            return;
        ConnectionId = id;
        var saved = _context.Service.Load().Connections.FirstOrDefault(c => c.Id == id);
        Name = saved?.DisplayName ?? "the server";

        if (_context.CheckPermissions is { } check && saved is not null)
        {
            var model = new PermissionCheckViewModel(token => check(saved, token), _context.Clipboard, Name);
            Check = model;
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
        foreach (var item in Steps)
        {
            item.IsReached = item.Step <= step;
            item.IsCurrent = item.Step == step;
        }
    }

    [RelayCommand]
    private void Next() => Show(FirstRunStep.Start);

    // ── Skip and finish ───────────────────────────────────────────────────

    [RelayCommand]
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
    [RelayCommand]
    [ReportsItsErrors]
    internal async Task FinishAsync()
    {
        if (ConnectionId is not { } id)
            return;
        try
        {
            if (StartMonitoring)
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
        Check?.Cancel();
        Closed?.Invoke();
    }
}
