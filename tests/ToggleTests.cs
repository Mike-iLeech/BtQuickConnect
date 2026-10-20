using Xunit;

namespace BtQuickConnect.Tests;

public class ToggleTests
{
    // все 8 сочетаний: пока идёт подключение или отключение, нажатие ничего не делает
    [Theory]
    [InlineData(false, false, false, TrayApp.ToggleAction.Connect)]
    [InlineData(true, false, false, TrayApp.ToggleAction.Disconnect)]
    [InlineData(false, true, false, TrayApp.ToggleAction.Wait)]
    [InlineData(true, true, false, TrayApp.ToggleAction.Wait)]
    [InlineData(false, false, true, TrayApp.ToggleAction.Wait)]     // профили ещё рвутся, огонёк уже оранжевый
    [InlineData(true, false, true, TrayApp.ToggleAction.Wait)]      // отключение отправлено, огонёк ещё зелёный
    [InlineData(false, true, true, TrayApp.ToggleAction.Wait)]
    [InlineData(true, true, true, TrayApp.ToggleAction.Wait)]
    public void Toggle(bool connected, bool connecting, bool disconnecting, object expected) =>
        Assert.Equal((TrayApp.ToggleAction)expected, TrayApp.DecideToggle(connected, connecting, disconnecting));
}
