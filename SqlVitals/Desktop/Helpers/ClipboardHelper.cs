using System.Runtime.InteropServices;
using System.Windows;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// Retries Clipboard.SetText to work around the transient CLIPBRD_E_CANT_OPEN (0x800401D0)
/// error that occurs when another process momentarily holds the clipboard open.
/// </summary>
internal static class ClipboardHelper
{
    private const int ClipboardErrorCode = unchecked((int)0x800401D0);
    private const int MaxRetries         = 5;
    private const int RetryDelayMs       = 50;

    /// <summary>
    /// Copies <paramref name="text"/> to the clipboard with retries.
    /// Shows a user-friendly warning if all attempts fail.
    /// </summary>
    public static void SetText(string text)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (COMException ex) when (ex.ErrorCode == ClipboardErrorCode)
            {
                if (attempt == MaxRetries - 1)
                {
                    CompositionRoot.Get<IDialogService>().Warn(
                        "Could not open the clipboard — another application is holding it.\nPlease try again.", "Copy Failed");
                    return;
                }
                System.Threading.Thread.Sleep(RetryDelayMs);
            }
            catch (Exception ex)
            {
                CompositionRoot.Get<IDialogService>().Warn($"Failed to copy to clipboard:\n{ex.Message}", "Copy Failed");
                return;
            }
        }
    }
}
