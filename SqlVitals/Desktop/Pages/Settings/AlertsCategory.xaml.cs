using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Settings → Alerts &amp; chat (#131), bound to its <see cref="AlertsCategoryViewModel"/> (#190). Only
/// view code here: the channel cards' width, and the channel form scrolled into view and the focus
/// put where its view model asks.
/// </summary>
internal partial class AlertsCategory : UserControl
{
    // The narrowest a channel card gets before they go one a row.
    private const double MinChannelWidth = 300;

    public static readonly DependencyProperty ChannelWidthProperty =
        DependencyProperty.Register(nameof(ChannelWidth), typeof(double), typeof(AlertsCategory), new PropertyMetadata(MinChannelWidth + 12));

    private ChatChannelForm? _form;

    public AlertsCategory(AlertsCategoryViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();

        model.PropertyChanged += OnModelChanged;
        ChannelCards.SizeChanged += (_, _) => UpdateChannelWidth();
        Loaded   += (_, _) => model.Activate();
        Unloaded += (_, _) => model.Deactivate();
    }

    public AlertsCategoryViewModel ViewModel { get; }

    public double ChannelWidth
    {
        get => (double)GetValue(ChannelWidthProperty);
        set => SetValue(ChannelWidthProperty, value);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AlertsCategoryViewModel.Form))
            return;
        _form?.FocusRequested -= Focus;
        _form = ViewModel.Form;
        if (_form is null)
            return;
        _form.FocusRequested += Focus;
        ChatChannelForm.BringIntoView();
    }

    // Once the form has laid out its boxes for the form just opened.
    private void Focus(string field) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        var box = field switch
        {
            "Url"     => TxtChatUrl,
            "PingUrl" => TxtPingUrl,
            _         => TxtChatName,
        };
        box.Focus();
    });

    private void UpdateChannelWidth()
    {
        var width = Math.Max(0, ChannelCards.ActualWidth);
        if (!double.IsFinite(width) || width == 0)
            return;
        var columns = width >= 2 * (MinChannelWidth + 12) ? 2 : 1;
        ChannelWidth = Math.Floor(width / columns);
    }
}
