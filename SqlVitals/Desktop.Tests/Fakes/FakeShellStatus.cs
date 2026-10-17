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

    /// <summary>Each read the loading bar would show, and how it ended (#117): "Load", "Load done", "Load ended".</summary>
    public List<string> Readings { get; } = [];

    /// <summary>What each read's Stop would do, in order.</summary>
    public List<Action?> Stops { get; } = [];

    public IReading ReportReading(object page, ReadingRequest request)
    {
        var name = request.Doing ?? request.Read.ToString();
        lock (Readings)
        {
            Readings.Add(name);
            Stops.Add(request.Stop);
        }
        return new Reading(this, name);
    }

    private sealed class Reading(FakeShellStatus shell, string name) : IReading
    {
        private bool _ended;

        public void Done() => End("done");

        public void Dispose() => End("ended");

        private void End(string how)
        {
            if (_ended)
                return;
            _ended = true;
            lock (shell.Readings)
                shell.Readings.Add($"{name} {how}");
        }
    }

    private void Add(object page, string report)
    {
        lock (Reports)
        {
            Reports.Add(report);
            From.Add(page);
        }
    }
}
