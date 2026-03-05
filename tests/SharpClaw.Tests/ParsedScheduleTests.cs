using SharpClaw.Agents;

namespace SharpClaw.Tests;

public class ParsedScheduleTests
{
    [Theory]
    [InlineData("every 5m", 5 * 60)]
    [InlineData("every 10min", 10 * 60)]
    [InlineData("every 1minutes", 1 * 60)]
    [InlineData("every 30m", 30 * 60)]
    public void Parse_MinuteIntervals_ReturnsCorrectTimespan(string schedule, int expectedSeconds)
    {
        var parsed = ParsedSchedule.Parse(schedule);
        Assert.NotNull(parsed.Interval);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), parsed.Interval.Value);
        Assert.False(parsed.RunOnce);
        Assert.Null(parsed.DailyAt);
    }

    [Theory]
    [InlineData("every 1h", 3600)]
    [InlineData("every 2hr", 7200)]
    [InlineData("every 24hours", 86400)]
    public void Parse_HourIntervals_ReturnsCorrectTimespan(string schedule, int expectedSeconds)
    {
        var parsed = ParsedSchedule.Parse(schedule);
        Assert.NotNull(parsed.Interval);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), parsed.Interval.Value);
    }

    [Fact]
    public void Parse_Once_SetsRunOnce()
    {
        var parsed = ParsedSchedule.Parse("once");
        Assert.True(parsed.RunOnce);
        Assert.Null(parsed.Interval);
        Assert.Null(parsed.DailyAt);
    }

    [Theory]
    [InlineData("daily 09:00", 9, 0)]
    [InlineData("daily 14:30", 14, 30)]
    [InlineData("daily 0:00", 0, 0)]
    [InlineData("daily 23:59", 23, 59)]
    public void Parse_DailySchedule_SetsCorrectTime(string schedule, int hour, int minute)
    {
        var parsed = ParsedSchedule.Parse(schedule);
        Assert.NotNull(parsed.DailyAt);
        Assert.Equal(new TimeOnly(hour, minute), parsed.DailyAt.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cron * * * * *")]
    [InlineData("weekly monday")]
    [InlineData("every")]
    [InlineData("every 5x")]
    public void Parse_InvalidSchedule_ThrowsFormatException(string schedule)
    {
        Assert.Throws<FormatException>(() => ParsedSchedule.Parse(schedule));
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var parsed = ParsedSchedule.Parse("EVERY 5M");
        Assert.NotNull(parsed.Interval);
        Assert.Equal(TimeSpan.FromMinutes(5), parsed.Interval.Value);
    }

    [Fact]
    public void GetNextDelay_Once_ReturnsZero()
    {
        var parsed = ParsedSchedule.Parse("once");
        Assert.Equal(TimeSpan.Zero, parsed.GetNextDelay());
    }

    [Fact]
    public void GetNextDelay_Interval_ReturnsInterval()
    {
        var parsed = ParsedSchedule.Parse("every 10m");
        Assert.Equal(TimeSpan.FromMinutes(10), parsed.GetNextDelay());
    }

    [Fact]
    public void GetNextDelay_Daily_ReturnsFutureDelay()
    {
        var parsed = ParsedSchedule.Parse("daily 23:59");
        var delay = parsed.GetNextDelay();
        Assert.True(delay > TimeSpan.Zero);
        Assert.True(delay <= TimeSpan.FromHours(24));
    }
}
