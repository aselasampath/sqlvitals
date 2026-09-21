using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SqlVitals.Installer.Core;

/// <summary>
/// Plain-text log in %TEMP% for troubleshooting. It records steps, paths and error messages
/// only — Setup never reads the app's configuration or saved connections, so there is nothing
/// sensitive to leak.
/// </summary>
public static class SetupLog
{
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"SqlVitals-Setup-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");

    public static void Info(string message)  => Write("INFO ", message);
    public static void Warn(string message)  => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path,
                    $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the install.
        }
    }
}
