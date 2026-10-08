using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>An <see cref="IShellStatus"/> that writes down each report (#183), as "Read Load", "Error Refresh: boom".</summary>
internal sealed class FakeShellStatus : IShellStatus
{
    public List<string> Reports { get; } = [];

    /// <summary>Who each report was about, in order.</summary>
    public List<object> From { get; } = [];

    public void ReportRead(object page, PageRead read) => Add(page, $"Read {read}");

    public void ReportError(object page, string source, Exception exception, PageRead read) =>
        Add(page, $"Error {read}: {exception.Message}");

    public void ReportContextChanged(object page) => Add(page, "Context");

    private void Add(object page, string report)
    {
        lock (Reports)
        {
            Reports.Add(report);
            From.Add(page);
        }
    }
}
