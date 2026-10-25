using ScreenTime.Core;

namespace ScreenTime.Tests;

public class DurationFormatTests
{
    [Theory]
    [InlineData(0, "0m")]
    [InlineData(-5, "0m")]
    [InlineData(1, "<1m")]
    [InlineData(59, "<1m")]
    [InlineData(60, "1m")]
    [InlineData(12 * 60 + 59, "12m")]
    [InlineData(3 * 3600, "3h")]
    [InlineData(5 * 3600 + 42 * 60 + 30, "5h 42m")]
    [InlineData(25 * 3600, "25h")]
    public void Formats_hours_and_minutes(int seconds, string expected) =>
        Assert.Equal(expected, DurationFormat.Short(TimeSpan.FromSeconds(seconds)));
}
