using System.Text.Json;
using Xunit;

namespace BtQuickConnect.Tests;

public class SettingsTests
{
    [Fact]
    public void OldSettingsFile_KeepsAutoUpdateOn()
    {
        // файл от версии без автообновления — поля AutoUpdate в нём нет
        var s = JsonSerializer.Deserialize<Settings>("""{"Address":"4072181F92CA","Name":"OnePlus Buds 3"}""")!;
        Assert.True(s.AutoUpdate);
        Assert.Null(s.Hotkey);
    }

    [Fact]
    public void Hotkey_RoundTrips()
    {
        var s = new Settings
        {
            Address = "X",
            AutoUpdate = false,
            Hotkey = new HotkeyBinding { Mouse = MouseButtons.XButton1, Modifiers = HotkeyBinding.ModCtrl },
        };
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;
        Assert.False(back.AutoUpdate);
        Assert.Equal(MouseButtons.XButton1, back.Hotkey!.Mouse);
        Assert.Equal(HotkeyBinding.ModCtrl, back.Hotkey.Modifiers);
    }

    [Theory]
    [InlineData(Keys.B, false)]
    [InlineData(Keys.D1, false)]
    [InlineData(Keys.Space, false)]
    [InlineData(Keys.F13, true)]
    [InlineData(Keys.F24, true)]
    [InlineData(Keys.MediaPlayPause, true)]
    [InlineData(Keys.Pause, true)]
    public void KeysWithoutModifiers(Keys k, bool allowed) =>
        Assert.Equal(allowed, HotkeyBinding.AllowedWithoutModifiers(k));

    [Fact]
    public void Binding_Names()
    {
        Assert.Equal("Ctrl + Alt + B",
            new HotkeyBinding { Key = Keys.B, Modifiers = HotkeyBinding.ModCtrl | HotkeyBinding.ModAlt }.ToString());
        Assert.Equal("Боковая кнопка мыши «Вперёд»", new HotkeyBinding { Mouse = MouseButtons.XButton2 }.ToString());
    }
}
