using System.Windows.Controls;

namespace SqlVitals.Desktop.Helpers;

/// <summary>A button that reads "…" and can't be pressed again while what it started runs: opening a plan.</summary>
public static class BusyButton
{
    public static async Task WhileAsync(Button button, Func<Task> work)
    {
        var label = button.Content;
        button.IsEnabled = false;
        button.Content   = "…";
        try
        {
            await work();
        }
        finally
        {
            button.IsEnabled = true;
            button.Content   = label;
        }
    }
}
