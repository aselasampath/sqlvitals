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
