using SqlVitals.Engine.Backups;

namespace SqlVitals.Engine.Tests.Backups;

public class BackupRpoTests
{
    [Fact]
    public void Defaults_are_a_week_for_full_and_an_hour_for_log()
    {
        Assert.Equal(TimeSpan.FromDays(7), BackupRpo.Default.FullMaxAge);
        Assert.Equal(TimeSpan.FromHours(1), BackupRpo.Default.LogMaxAge);
        Assert.Equal("full backups older than 7 d, log backups older than 1 h", BackupRpo.Default.Describe());
    }

    [Theory]
    [InlineData("7",        "15",      7 * 24 * 60, 15)]         // bare numbers: days, minutes
    [InlineData("26h",      "2h",      26 * 60,     120)]
    [InlineData("1d 12h",   "90 min",  36 * 60,     90)]
    [InlineData(" 2 days ", "1 hour",  2 * 24 * 60, 60)]
    [InlineData("3D12H",    "45M",     84 * 60,     45)]
    [InlineData("1 day 30 mins", "1h 30m", 24 * 60 + 30, 90)]
    public void TryParse_reads_numbers_with_or_without_units(string full, string log, int fullMinutes, int logMinutes)
    {
        Assert.True(BackupRpo.TryParse(full, log, out var rpo, out var error), error);
        Assert.Equal(TimeSpan.FromMinutes(fullMinutes), rpo!.FullMaxAge);
        Assert.Equal(TimeSpan.FromMinutes(logMinutes), rpo.LogMaxAge);
    }

    [Theory]
    [InlineData("", "60")]
    [InlineData("abc", "60")]
    [InlineData("7x", "60")]
    [InlineData("1 12", "60")]        // two bare numbers: which units?
    [InlineData("-7", "60")]
    [InlineData("30 min", "60")]      // under the 1 h minimum
    [InlineData("401", "60")]         // over 400 days
    [InlineData("7", "")]
    [InlineData("7", "0")]
    [InlineData("7", "31d")]
    [InlineData("7", "1.5h")]
    public void TryParse_rejects_what_cant_be_used(string full, string log)
    {
        Assert.False(BackupRpo.TryParse(full, log, out var rpo, out var error));
        Assert.Null(rpo);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void The_error_says_which_box_is_wrong()
    {
        BackupRpo.TryParse("x", "60", out _, out var fullError);
        BackupRpo.TryParse("7", "x", out _, out var logError);

        Assert.Contains("full backup", fullError);
        Assert.Contains("log backup", logError);
    }

    [Theory]
    [InlineData(1,                "1 min")]
    [InlineData(60,               "1 h")]
    [InlineData(90,               "90 min")]
    [InlineData(200,              "3 h 20 min")]
    [InlineData(26 * 60,          "26 h")]
    [InlineData(24 * 60,          "1 d")]
    [InlineData(7 * 24 * 60,      "7 d")]
    [InlineData(84 * 60,          "3 d 12 h")]
    [InlineData(2 * 1440 + 5,     "2 d 5 min")]
    public void Format_shows_the_largest_units_that_are_exact(int minutes, string expected) =>
        Assert.Equal(expected, BackupRpo.Format(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(1)]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(200)]
    [InlineData(26 * 60)]
    [InlineData(84 * 60)]
    [InlineData(2 * 1440 + 5)]
    [InlineData(400 * 1440)]
    public void What_Format_shows_parses_back_to_the_same_age(int minutes)
    {
        var age = TimeSpan.FromMinutes(minutes);
        Assert.Equal(age, BackupRpo.TryParseAge(BackupRpo.Format(age), TimeSpan.FromDays(1)));
    }

    [Theory]
    [InlineData(0,        7 * 24 * 60)]       // missing from an older settings file
    [InlineData(-5,       7 * 24 * 60)]
    [InlineData(30,       7 * 24 * 60)]       // under 1 h
    [InlineData(1440,     1440)]
    [InlineData(int.MaxValue, 7 * 24 * 60)]
    public void NormalizeFull_falls_back_to_the_default(int saved, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), BackupRpo.NormalizeFull(saved));

    [Theory]
    [InlineData(0,  60)]
    [InlineData(15, 15)]
    [InlineData(31 * 1440, 60)]
    public void NormalizeLog_falls_back_to_the_default(int saved, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), BackupRpo.NormalizeLog(saved));
}
