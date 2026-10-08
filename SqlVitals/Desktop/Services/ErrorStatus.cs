using SqlVitals.Engine.Errors;

namespace SqlVitals.Desktop.Services;

/// <summary>An error as one line, for the top bar and a verdict that couldn't be read.</summary>
public static class ErrorStatus
{
    /// <summary>
    /// "Error [WaitStatsRepository.GetCumulativeWaitsAsync]: Timeout", without secrets. A failure
    /// SqlVitals tells apart (#167) says what it was in plain words: "The login lacks VIEW SERVER STATE."
    /// </summary>
    public static string Format(Exception ex) =>
        ConnectionSettingsService.RedactSecrets(ex is WaitStatsException wse
            ? $"Error [{wse.ErrorTag}]: {wse.Failure.Explanation ?? ex.InnerException?.Message ?? ex.Message}"
            : $"Error: {ex.Message}");

    /// <summary>The script that fixes a permission failure (#167), for a DBA to run; null for any other error.</summary>
    public static string? Fix(Exception ex) => (ex as WaitStatsException)?.FixScript;
}
