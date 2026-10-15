using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Helpers;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// What a tab says when it has nothing to show, e.g. "Query Store is off for Sales", with the
/// statement that fixes it to copy. SqlVitals never runs the statement.
/// </summary>
public partial class ScriptNotice : UserControl
{
    public ScriptNotice() => InitializeComponent();

    /// <summary>The message bound from a view model (#189): shown while set, hidden while null.</summary>
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(ScriptNotice), new PropertyMetadata(null, OnNoticeChanged));

    /// <summary>The statement to copy with <see cref="Message"/>, if any.</summary>
    public static readonly DependencyProperty ScriptProperty = DependencyProperty.Register(
        nameof(Script), typeof(string), typeof(ScriptNotice), new PropertyMetadata(null, OnNoticeChanged));

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string? Script
    {
        get => (string?)GetValue(ScriptProperty);
        set => SetValue(ScriptProperty, value);
    }

    private static void OnNoticeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var notice = (ScriptNotice)d;
        if (notice.Message is { } message)
            notice.Show(message, notice.Script);
        else
            notice.Hide();
    }

    /// <summary>Shows the message, and the script when there is one.</summary>
    public void Show(string message, string? script = null)
    {
        TxtMessage.Text        = message;
        TxtScript.Text         = script ?? string.Empty;
        ScriptPanel.Visibility = string.IsNullOrEmpty(script) ? Visibility.Collapsed : Visibility.Visible;
        Visibility             = Visibility.Visible;
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (TxtScript.Text.Length > 0)
            ClipboardHelper.SetText(TxtScript.Text);
    }
}
