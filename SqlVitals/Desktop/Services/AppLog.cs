using System.Globalization;
using System.IO;
using System.Text;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Plain-text application log, one file per day under %APPDATA%\SqlVitals\Logs. Messages go
/// through <see cref="ConnectionSettingsService.RedactSecrets"/> because SQL errors can echo
/// parts of a connection string back.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlVitals", "Logs");

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var now  = DateTime.Now;
            var file = Path.Combine(Directory, $"SqlVitals-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
            var line = $"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} " +
                       $"{ConnectionSettingsService.RedactSecrets(message)}{Environment.NewLine}";

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}
