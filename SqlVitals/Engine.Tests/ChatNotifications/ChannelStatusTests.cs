using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Tests.ChatNotifications;

public sealed class ChannelStatusTests
{
    [Fact]
    public void ANewChannel_IsNeverUsed()
    {
        Assert.Equal("Never used", ChannelStatus.NeverUsed.Text(DateTime.Now));
    }

    [Fact]
    public void APostToday_ShowsTheTime()
    {
        var now = new DateTime(2026, 9, 30, 14, 0, 0, DateTimeKind.Local);
        var posted = new DateTime(2026, 9, 30, 10, 42, 0, DateTimeKind.Local).ToUniversalTime();

        Assert.Equal("Last posted 10:42", new ChannelStatus(posted).Text(now));
    }

    [Fact]
    public void APostOnAnotherDay_ShowsTheDateToo()
    {
        var now = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Local);
        var posted = new DateTime(2026, 9, 30, 10, 42, 0, DateTimeKind.Local).ToUniversalTime();

        var text = new ChannelStatus(posted).Text(now);

        Assert.StartsWith("Last posted 30 ", text);
        Assert.EndsWith(" 10:42", text);
    }
}
