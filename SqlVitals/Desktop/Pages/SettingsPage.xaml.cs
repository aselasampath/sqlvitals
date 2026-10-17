using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlVitals.Desktop.Pages.Settings;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Settings (#131), bound to its <see cref="SettingsViewModel"/> (#190): a side menu of categories in
/// groups, and the category beside it. Only view code here: which view shows each category's view
/// model (made once, so a category opened again is the same, scrolled where it was), and the
/// first-run flow's.
/// </summary>
public partial class SettingsPage : Page, IPageContext
{
    private readonly Dictionary<SettingsCategoryViewModel, FrameworkElement> _views = new();

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PropertyChanged += OnModelChanged;
        ShowCategory();
        ShowFirstRun();

        Loaded   += (_, _) => ViewModel.Activate();
        Unloaded += (_, _) => ViewModel.Deactivate();
    }

    internal SettingsViewModel ViewModel { get; }

    // The app's own settings, not a view of a server's data: no badge in the top bar (#140).
    public PageKind Kind => PageKind.None;

    /// <summary>The category on screen.</summary>
    public SettingsCategory Category => ViewModel.Category;

    /// <summary>The category's view on screen.</summary>
    internal FrameworkElement? Shown => CategoryHost.Content as FrameworkElement;

    /// <summary>The first-run flow (#177) while it is shown; null otherwise.</summary>
    internal FirstRunFlow? FirstRun => FlowHost.Content as FirstRunFlow;

    /// <summary>Connections' view, once it has been opened.</summary>
    internal ConnectionsCategory? Connections =>
        ViewModel.Connections is { } model ? _views.GetValueOrDefault(model) as ConnectionsCategory : null;

    /// <inheritdoc cref="SettingsViewModel.OpenAsync"/>
    public Task OpenAsync(SettingsLocation location) => ViewModel.OpenAsync(location);

    /// <inheritdoc cref="SettingsViewModel.ShowMessage"/>
    public void ShowMessage(string? message) => ViewModel.ShowMessage(message);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.Current):  ShowCategory(); break;
            case nameof(SettingsViewModel.FirstRun): ShowFirstRun(); break;
        }
    }

    private void ShowCategory()
    {
        if (ViewModel.Current is not { } model)
            return;
        if (!_views.TryGetValue(model, out var view))
            _views[model] = view = ViewFor(model);
        CategoryHost.Content = view;
        AutomationProperties.SetName(CategoryHost, SettingsNav.Info(ViewModel.Category).Title);
    }

    private static FrameworkElement ViewFor(SettingsCategoryViewModel model) => model switch
    {
        ConnectionsCategoryViewModel connections   => new ConnectionsCategory(connections),
        MonitoringCategoryViewModel monitoring     => new MonitoringCategory(monitoring),
        ThresholdsCategoryViewModel thresholds     => new ThresholdsCategory(thresholds),
        AlertsCategoryViewModel alerts             => new AlertsCategory(alerts),
        NotificationAreaCategoryViewModel tray     => new NotificationAreaCategory(tray),
        ActivityCategoryViewModel activity         => new ActivityCategory(activity),
        CollectorCategoryViewModel collector       => new CollectorCategory(collector),
        AppearanceCategoryViewModel appearance     => new AppearanceCategory(appearance),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model.GetType().Name, "No view for this category."),
    };

    private void ShowFirstRun() =>
        FlowHost.Content = ViewModel.FirstRun is { } flow ? new FirstRunFlow(flow) : null;
}
