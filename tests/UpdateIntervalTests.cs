using System.Text.Json;
using Xunit;

namespace BtQuickConnect.Tests;

public class UpdateIntervalTests
{
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(3, 3.0)]
    [InlineData(24, 24.0)]
    [InlineData(5, 5.0)]        // значение не из меню (правили файл руками) — как есть
    [InlineData(10_000, 168.0)] // не реже раза в неделю — и Timer.Interval не переполнится
    public void CheckInterval_Hours(int hours, double expected) =>
        Assert.Equal(TimeSpan.FromHours(expected), TrayApp.CheckInterval(hours));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CheckInterval_Off(int hours) => Assert.Null(TrayApp.CheckInterval(hours));

    [Fact]
    public void Choices_IncludeOff_AndFitTimer()
    {
        Assert.Contains(0, TrayApp.UpdateCheckChoices);
        foreach (int h in TrayApp.UpdateCheckChoices)
            if (TrayApp.CheckInterval(h) is { } t) Assert.True(t.TotalMilliseconds <= int.MaxValue);
    }

    [Theory]
    [InlineData(0, "Никогда")]
    [InlineData(1, "Раз в час")]
    [InlineData(6, "Раз в 6 ч")]
    [InlineData(24, "Раз в сутки")]
    public void Names(int hours, string name) => Assert.Equal(name, TrayApp.IntervalName(hours));

    [Fact]
    public void OldSettingsFile_DefaultsToHourly()
    {
        var s = JsonSerializer.Deserialize<Settings>("""{"Address":"4072181F92CA","AutoUpdate":true}""")!;
        Assert.Equal(1, s.UpdateCheckHours);
    }

    [Fact]
    public void Off_RoundTrips()
    {
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { UpdateCheckHours = 0 }))!;
        Assert.Equal(0, back.UpdateCheckHours);
        Assert.Null(TrayApp.CheckInterval(back.UpdateCheckHours));
    }
}
