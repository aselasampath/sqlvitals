using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SqlVitals.Desktop.ViewModels.Shell;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// Draws the <see cref="LoadingBarViewModel"/> (#117): the stripe moving across the top of the
/// page area while SqlVitals waits, still where Windows has animations turned off, and the line
/// under it with Stop. A screen reader hears it appear and end through a live region.
/// </summary>
public partial class LoadingBar : UserControl
{
    private static readonly Duration Sweep = new(TimeSpan.FromSeconds(1.4));

    private LoadingBarViewModel? _model;

    // The stripe is sweeping: Stop being offered or not leaves it where it is.
    private bool _sweeping;

    public LoadingBar()
    {
        InitializeComponent();
        Track.SizeChanged += (_, _) => Animate();
    }

    /// <summary>The stripe moves; off in tests, and where Windows has animations turned off.</summary>
    internal bool AnimatesStripe { get; set; } = SystemParameters.ClientAreaAnimation;

    /// <summary>The bar the shell shows.</summary>
    public LoadingBarViewModel? ViewModel
    {
        get => _model;
        set
        {
            _model?.PropertyChanged -= Model_PropertyChanged;
            _model = value;
            DataContext = value;
            value?.PropertyChanged += Model_PropertyChanged;
            Show();
        }
    }

    /// <summary>What the line says, with Stop when it's offered; empty while hidden. For tests.</summary>
    internal string Text => Line.Visibility != Visibility.Visible ? string.Empty
        : TxtLoading.Text + (BtnStop.Visibility == Visibility.Visible ? " [Stop]" : "");

    /// <summary>What a screen reader heard last; for tests.</summary>
    internal string Announced => TxtAnnounce.Text;

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LoadingBarViewModel.IsShown) or nameof(LoadingBarViewModel.CanStop):
                Show();
                break;
            case nameof(LoadingBarViewModel.Announcement):
                Announce(_model!.Announcement);
                break;
        }
    }

    private void Show()
    {
        var shown = _model?.IsShown == true;
        Track.Visibility   = shown ? Visibility.Visible : Visibility.Collapsed;
        Line.Visibility    = shown ? Visibility.Visible : Visibility.Collapsed;
        BtnStop.Visibility = _model?.CanStop == true ? Visibility.Visible : Visibility.Collapsed;
        if (shown != _sweeping)
            Animate();
    }

    // The stripe sweeps across while the bar shows, and fills the track, still, without animations.
    // Started again only as the bar comes or its width changes, so the sweep doesn't jump back.
    private void Animate()
    {
        var moving = _model?.IsShown == true && AnimatesStripe && Track.ActualWidth > 0;
        _sweeping = moving;
        if (!moving)
        {
            StripeShift.BeginAnimation(TranslateTransform.XProperty, null);
            StripeShift.X = 0;
            Stripe.HorizontalAlignment = HorizontalAlignment.Stretch;
            Stripe.Width = double.NaN;
            return;
        }

        var width = Math.Max(80, Track.ActualWidth / 4);
        Stripe.HorizontalAlignment = HorizontalAlignment.Left;
        Stripe.Width = width;
        StripeShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-width, Track.ActualWidth, Sweep) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void Announce(string text)
    {
        TxtAnnounce.Text = text;
        AutomationProperties.SetName(TxtAnnounce, text);
        if (text.Length > 0 && UIElementAutomationPeer.CreatePeerForElement(TxtAnnounce) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
