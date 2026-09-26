using SqlVitals.Engine.FileIo;

namespace SqlVitals.Engine.Tests.FileIo;

public class FileLatencyThresholdsTests
{
    [Fact]
    public void Defaults_are_20_ms_for_data_and_10_ms_for_log()
    {
        Assert.Equal(20, FileLatencyThresholds.Default.DataMs);
        Assert.Equal(10, FileLatencyThresholds.Default.LogMs);
        Assert.Equal("data files over 20 ms, log files over 10 ms", FileLatencyThresholds.Default.Describe());
    }

    [Fact]
    public void Log_files_get_the_log_threshold_and_everything_else_the_data_one()
    {
        var thresholds = new FileLatencyThresholds(25, 3);

        Assert.Equal(3, thresholds.For(isLog: true));
        Assert.Equal(25, thresholds.For(isLog: false));
    }

    [Theory]
    [InlineData("20",      "10",      20,   10)]
    [InlineData("20 ms",   "2.5ms",   20,   2.5)]
    [InlineData(" 15 MS ", "1,5",     15,   1.5)]      // a comma as the decimal separator
    [InlineData("0.5",     "10000",   0.5,  10000)]    // the limits
    [InlineData("100 milliseconds", "5 msec", 100, 5)]
    public void TryParse_reads_milliseconds_with_or_without_the_unit(string data, string log, double dataMs, double logMs)
    {
        Assert.True(FileLatencyThresholds.TryParse(data, log, out var thresholds, out var error), error);
        Assert.Equal(dataMs, thresholds!.DataMs);
        Assert.Equal(logMs, thresholds.LogMs);
    }

    [Theory]
    [InlineData("", "10")]
    [InlineData("abc", "10")]
    [InlineData("-5", "10")]
    [InlineData("0", "10")]           // under the 0.5 ms minimum
    [InlineData("0.4", "10")]
    [InlineData("10001", "10")]       // over 10 s
    [InlineData("20 s", "10")]        // only milliseconds
    [InlineData("1,000", "10")]       // a thousands separator, not a decimal
    [InlineData("20", "")]
    [InlineData("20", "1.234")]
    public void TryParse_rejects_what_cant_be_used(string data, string log)
    {
        Assert.False(FileLatencyThresholds.TryParse(data, log, out var thresholds, out var error));
        Assert.Null(thresholds);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void The_error_says_which_box_is_wrong()
    {
        FileLatencyThresholds.TryParse("x", "10", out _, out var dataError);
        FileLatencyThresholds.TryParse("20", "x", out _, out var logError);

        Assert.Contains("data file", dataError);
        Assert.Contains("log file", logError);
    }

    [Theory]
    [InlineData(20,   "20 ms")]
    [InlineData(2.5,  "2.5 ms")]
    [InlineData(0.75, "0.75 ms")]
    [InlineData(10000, "10000 ms")]
    public void Format_reads_back_to_the_same_value(double ms, string expected)
    {
        Assert.Equal(expected, FileLatencyThresholds.Format(ms));
        Assert.Equal(ms, FileLatencyThresholds.TryParseMs(expected));
    }

    [Theory]
    [InlineData(0,               20)]      // missing from an older settings file
    [InlineData(-1,              20)]
    [InlineData(0.1,             20)]
    [InlineData(double.NaN,      20)]
    [InlineData(double.PositiveInfinity, 20)]
    [InlineData(50_000,          20)]
    [InlineData(35,              35)]
    public void NormalizeData_falls_back_to_the_default(double saved, double expected) =>
        Assert.Equal(expected, FileLatencyThresholds.NormalizeData(saved));

    [Theory]
    [InlineData(0,   10)]
    [InlineData(2.5, 2.5)]
    public void NormalizeLog_falls_back_to_the_default(double saved, double expected) =>
        Assert.Equal(expected, FileLatencyThresholds.NormalizeLog(saved));
}
