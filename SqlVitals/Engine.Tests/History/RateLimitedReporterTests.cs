using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Tests.History;

public class RateLimitedReporterTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly List<string> _logged = new();

    private RateLimitedReporter Create() => new((message, _) => _logged.Add(message), _time);

    [Fact]
    public void Report_PassesOnOnePerIntervalAndCountsTheRest()
    {
        var reporter = Create();
        var ex = new IOException("disk full");

        reporter.Report("first", ex);
        reporter.Report("second", ex);
        reporter.Report("third", ex);
        _time.Advance(RateLimitedReporter.Interval);
        reporter.Report("fourth", ex);

        Assert.Equal(new[] { "first", "fourth (2 similar error(s) not logged)" }, _logged);
    }

    [Fact]
    public void Report_SwallowsAFailingLogger()
    {
        var reporter = new RateLimitedReporter((_, _) => throw new IOException("log locked"), _time);

        reporter.Report("anything", new Exception());   // must not throw
    }
}
