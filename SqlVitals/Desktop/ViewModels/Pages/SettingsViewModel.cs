using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Settings;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Settings (#131): a side menu of categories in groups, and the category beside it, each with its
/// view model under <c>ViewModels/Settings</c> (#190). A category is built the first time it is
/// opened and kept while Settings is open, so what's typed in it stays. Leaving a category, or
/// Settings, with unsaved changes asks first through <see cref="IDialogService"/>. It reads nothing
/// on Refresh.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsContext _context;
    private readonly Dictionary<SettingsCategory, SettingsCategoryViewModel> _built = new();
    private bool _active;

    internal SettingsViewModel(SettingsContext context, SettingsLocation? location = null, string? message = null)
    {
        _context = context;
        context.Open       = target => _ = ReportErrorsOf(OpenAsync(target));
        context.OpenServer = id => ServerRequested?.Invoke(id);

        Groups = SettingsNav.Groups
            .Select((group, i) => new SettingsMenuGroup(group.Name.ToUpperInvariant(), i == 0,
                group.Categories.Select(info => new SettingsMenuItem(info, OpenCategoryCommand)).ToList()))
            .ToList();
        UpdateCount();
        Message = string.IsNullOrWhiteSpace(message) ? null : message;

        // Nothing is open yet, so there's nothing to leave: this completes straight away.
        _ = OpenAsync(location ?? new SettingsLocation(SettingsCategory.Connections));
    }

    /// <summary>"View on Home" on a connection's page, or the first run's last step: that server's Overview (#143).</summary>
    public event Action<Guid>? ServerRequested;

    /// <summary>The theme was switched: open Settings again on Appearance, in the new theme.</summary>
    public event Action? ThemeChanged;

    /// <summary>A category was opened, so Settings can open on it again this session.</summary>
    public event Action<SettingsCategory>? CategoryShown;

    // Settings reads nothing: what it shows is the settings file, read by each category.
    protected override Task LoadAsync(CancellationToken cancel) => Task.CompletedTask;

    // ── The side menu ─────────────────────────────────────────────────────────

    public IReadOnlyList<SettingsMenuGroup> Groups { get; }

    public SettingsMenuItem MenuItemFor(SettingsCategory category) => Groups.SelectMany(g => g.Items).Single(i => i.Category == category);

    // A click, Space or Enter; on the category already open it goes back to its start, e.g. from a
    // connection's page to the cards.
    // Never waiting for the one before, as a click didn't before #190: leaving a category may save
    // a connection, which signs in first.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenCategoryAsync(SettingsCategory category) => ReportErrorsOf(OpenAsync(new SettingsLocation(category)));

    // The menu shows the category on screen; a click the user took back shows it again.
    private void ShowSelection()
    {
        foreach (var item in Groups.SelectMany(g => g.Items))
        {
            item.IsSelected = item.Category == Category;
            item.ShowSelection();
        }
    }

    // "Connections  5".
    private void UpdateCount()
    {
        var count = _context.Service.Load().Connections.Count;
        var item  = MenuItemFor(SettingsCategory.Connections);
        item.Count          = count.ToString(CultureInfo.CurrentCulture);
        item.AutomationName = count == 1 ? "Connections, 1 saved" : $"Connections, {count} saved";
    }

    /// <summary>The page is on screen: the count follows the settings.</summary>
    public void Activate()
    {
        if (_active)
            return;
        _active = true;
        _context.Service.Changed += OnSettingsChanged;
    }

    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _context.Service.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => _context.Ui.Post(UpdateCount);

    // ── The category ──────────────────────────────────────────────────────────

    /// <summary>The category on screen.</summary>
    [ObservableProperty]
    private SettingsCategory _category;

    /// <summary>The category's view model on screen.</summary>
    [ObservableProperty]
    private SettingsCategoryViewModel? _current;

    /// <summary>Connections, once it has been opened.</summary>
    public ConnectionsCategoryViewModel? Connections =>
        _built.GetValueOrDefault(SettingsCategory.Connections) as ConnectionsCategoryViewModel;

    /// <summary>A category's view model, once it has been opened.</summary>
    public SettingsCategoryViewModel? Built(SettingsCategory category) => _built.GetValueOrDefault(category);

    /// <summary>
    /// Whether Settings can be left for another page. With unsaved changes on a connection's page
    /// or the default thresholds, it asks to save, discard or stay; false when the user stays.
    /// </summary>
    [ReportsItsErrors]
    public Task<bool> TryLeaveAsync() =>
        FirstRun is { } flow ? flow.TryLeaveAsync()
        : Current is { } category ? category.TryLeaveAsync()
        : Task.FromResult(true);

    /// <summary>Opens a category, or a connection's tab, leaving the one on screen first.</summary>
    [ReportsItsErrors]
    public async Task OpenAsync(SettingsLocation location)
    {
        if (Current is not null && location.Category != Category && !await TryLeaveAsync())
        {
            ShowSelection();
            return;
        }

        if (!_built.TryGetValue(location.Category, out var model))
            _built[location.Category] = model = Build(location.Category);
        else if (!ReferenceEquals(model, Current))
            model.OnShownAgain();

        Category = location.Category;
        Current  = model;
        ShowSelection();
        CategoryShown?.Invoke(Category);

        if (model is ConnectionsCategoryViewModel connections)
            await connections.ShowAsync(location);

        // First run (#177): only while nothing is saved, so Back to it later is Connections.
        if (location.FirstRun && FirstRun is null && _context.Service.Load().Connections.Count == 0)
            ShowFirstRun();
    }

    private SettingsCategoryViewModel Build(SettingsCategory category)
    {
        switch (category)
        {
            case SettingsCategory.Connections:      return new ConnectionsCategoryViewModel(_context);
            case SettingsCategory.Monitoring:       return new MonitoringCategoryViewModel(_context);
            case SettingsCategory.Thresholds:       return new ThresholdsCategoryViewModel(_context);
            case SettingsCategory.Alerts:           return new AlertsCategoryViewModel(_context);
            case SettingsCategory.NotificationArea: return new NotificationAreaCategoryViewModel(_context);
            case SettingsCategory.Activity:         return new ActivityCategoryViewModel(_context);
            case SettingsCategory.Collector:        return new CollectorCategoryViewModel(_context);
            default:
                var appearance = new AppearanceCategoryViewModel(_context);
                appearance.ThemeChanged += () => ThemeChanged?.Invoke();
                return appearance;
        }
    }

    // ── First run (#177) ──────────────────────────────────────────────────────

    /// <summary>The first-run flow over the whole page while it is shown; null otherwise.</summary>
    [ObservableProperty]
    private FirstRunFlowViewModel? _firstRun;

    private void ShowFirstRun()
    {
        var flow = new FirstRunFlowViewModel(_context);
        flow.Closed += () =>
        {
            FirstRun = null;
            _ = Connections?.ShowAsync(new SettingsLocation(SettingsCategory.Connections));
        };
        FirstRun = flow;
    }

    // ── A message from elsewhere ──────────────────────────────────────────────

    /// <summary>Above the category, e.g. "Switched to UAT." or a password prompt; null hides it.</summary>
    [ObservableProperty]
    private string? _message;

    public void ShowMessage(string? message) => Message = string.IsNullOrWhiteSpace(message) ? null : message;

    [RelayCommand]
    private void CloseMessage() => Message = null;
}
