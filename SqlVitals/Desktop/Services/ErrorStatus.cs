using SqlVitals.Engine.Errors;

namespace SqlVitals.Desktop.Services;

/// <summary>An error as one line, for the top bar and a verdict that couldn't be read.</summary>
public static class ErrorStatus
{
    /// <summary>"Error [WaitStatsRepository.GetCumulativeWaitsAsync]: Timeout", without secrets.</summary>
    public static string Format(Exception ex) =>
        ConnectionSettingsService.RedactSecrets(ex is WaitStatsException wse
            ? $"Error [{wse.ErrorTag}]: {ex.InnerException?.Message ?? ex.Message}"
            : $"Error: {ex.Message}");
}
