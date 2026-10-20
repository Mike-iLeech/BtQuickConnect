using Xunit;

namespace BtQuickConnect.Tests;

public class ToggleTests
{
    [Theory]
    [InlineData(false, false, false, TrayApp.ToggleAction.Connect)]
    [InlineData(true, false, false, TrayApp.ToggleAction.Disconnect)]
    [InlineData(false, true, false, TrayApp.ToggleAction.Wait)]     // идёт подключение
    [InlineData(true, false, true, TrayApp.ToggleAction.Wait)]      // идёт отключение, огонёк ещё зелёный
    public void Toggle(bool connected, bool connecting, bool disconnecting, object expected) =>
        Assert.Equal((TrayApp.ToggleAction)expected, TrayApp.DecideToggle(connected, connecting, disconnecting));
}
