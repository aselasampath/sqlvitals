using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The banner over a page opened as evidence (#145): "Opened from alert", what it was opened for
/// and what the page shows of it, Show all, and for an alert that has ended on a page that only
/// shows now, a link to the history page set to the alert's window.
/// </summary>
public partial class FocusBannerView : UserControl
{
    public FocusBannerView() => InitializeComponent();

    /// <summary>Show all: the page clears its filter.</summary>
    public event Action? ShowAllRequested;

    /// <summary>The history page, set to the alert's window.</summary>
    public event Action? HistoryRequested;

    // Hidden with × for the page on screen: it stays hidden as the page updates its words.
    private bool _dismissed;

    /// <summary>A new page: a banner hidden on the previous one shows again.</summary>
    public void NewPage() => _dismissed = false;

    /// <summary>Shows the banner's words; hides it for null.</summary>
    /// <param name="showAll">Show all's words, or null when the page has no filter to clear.</param>
    public void Show(FocusBannerText? text, string? showAll)
    {
        if (text is null || _dismissed)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        RunTitle.Text = text.Title;
        RunWhat.Text  = text.What;
        AutomationProperties.SetName(Frame, $"{text.Title}: {text.What}");

        RunShowAll.Text        = showAll ?? "Show all";
        ShowAllCell.Visibility = showAll is null ? Visibility.Collapsed : Visibility.Visible;

        RunPast.Text           = text.Past ?? string.Empty;
        RunHistory.Text        = text.HistoryLink ?? string.Empty;
        LinkHistory.IsEnabled  = text.HistoryLink is not null;
        RunGap.Text            = text.HistoryLink is null ? string.Empty : "  ";
        PastLine.Visibility    = text.Past is null ? Visibility.Collapsed : Visibility.Visible;

        Visibility = Visibility.Visible;
    }

    /// <summary>What the banner says, as one line; for tests.</summary>
    internal string Text => Visibility != Visibility.Visible ? string.Empty
        : $"{RunTitle.Text} {RunWhat.Text}" + (ShowAllCell.Visibility == Visibility.Visible ? $" [{RunShowAll.Text}]" : "")
          + (PastLine.Visibility == Visibility.Visible ? $" | {RunPast.Text}" + (RunHistory.Text.Length > 0 ? $" [{RunHistory.Text}]" : "") : "");

    internal void ClickShowAll() => ShowAllRequested?.Invoke();

    internal void ClickHistory() => HistoryRequested?.Invoke();

    private void ShowAll_Click(object sender, RoutedEventArgs e) => ShowAllRequested?.Invoke();

    private void History_Click(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke();

    // The page stays as it is: only the banner goes, until the page is opened again.
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _dismissed = true;
        Visibility = Visibility.Collapsed;
    }
}
