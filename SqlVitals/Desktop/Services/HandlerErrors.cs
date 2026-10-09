namespace SqlVitals.Desktop.Services;

/// <summary>
/// How an <c>async void</c> event handler outside a page awaits work that can throw (#169): what
/// it throws is logged and shown, saying what failed, instead of reaching the app's last-resort
/// handler. A page's handler awaits <c>ViewModel.ReportErrorsOf</c> instead, which says it on the
/// top bar.
/// </summary>
public static class HandlerErrors
{
    /// <summary>Awaits <paramref name="work"/>; a failure is logged and shown in a message box. A cancellation says nothing.</summary>
    /// <param name="what">What failed, as the message says it: "save the connection".</param>
    [ReportsItsErrors]
    public static async Task Reported(this Task work, string what)
    {
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
            // Cancelled on purpose: nothing to say.
        }
        catch (Exception ex)
        {
            Show(what, ex);
        }
    }

    /// <summary>As <see cref="Reported(Task, string)"/>, for work with a result: the default after a failure.</summary>
    [ReportsItsErrors]
    public static async Task<T?> Reported<T>(this Task<T> work, string what)
    {
        try
        {
            return await work;
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (Exception ex)
        {
            Show(what, ex);
            return default;
        }
    }

    private static void Show(string what, Exception ex)
    {
        AppLog.Error($"Could not {what}", ex);
        CompositionRoot.Get<IDialogService>().Error(
            $"Could not {what}:\n\n{ConnectionSettingsService.RedactSecrets(ex.Message)}", "SqlVitals");
    }
}
