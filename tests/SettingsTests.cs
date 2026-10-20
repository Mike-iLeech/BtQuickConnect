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

    private static object[] K(Keys k, int mods = 0) => new object[] { k, MouseButtons.None, mods };
    private static object[] M(MouseButtons b, int mods = 0) => new object[] { Keys.None, b, mods };
    private const int C = HotkeyBinding.ModCtrl, A = HotkeyBinding.ModAlt, S = HotkeyBinding.ModShift, W = HotkeyBinding.ModWin;

    public static IEnumerable<object[]> Bindings => new (object[] b, bool ok)[]
    {
        (K(Keys.B, C | A), true),
        (K(Keys.F13), true),
        (K(Keys.MediaPlayPause), true),
        (K(Keys.F9, S), true),
        (K(Keys.B, W), true),
        (K(Keys.B), false),
        (K(Keys.B, S), false),          // Shift+буква = заглавная буква
        (K(Keys.D1, S), false),         // «!»
        (K(Keys.Space, S), false),
        (K(Keys.OemPeriod, S), false),
        (K(Keys.Escape), false),
        (K(Keys.F4, A), false),         // закрыть окно
        (K(Keys.Tab, A), false),
        (K(Keys.C, C), false),
        (K(Keys.V, C), false),
        (K(Keys.Delete, C | A), false),
        (M(MouseButtons.XButton1), true),
        (M(MouseButtons.Middle, C), true),
        (M(MouseButtons.Middle), false), // вкладки и ссылки
        (M(MouseButtons.Left, C), false),
    }.Select(x => x.b.Append(x.ok).ToArray());

    [Theory]
    [MemberData(nameof(Bindings))]
    public void Validate(Keys key, MouseButtons mouse, int mods, bool ok) =>
        Assert.Equal(ok, HotkeyBinding.Validate(new HotkeyBinding { Key = key, Mouse = mouse, Modifiers = mods }) == null);

    [Fact]
    public void Binding_Names()
    {
        Assert.Equal("Ctrl + Alt + B",
            new HotkeyBinding { Key = Keys.B, Modifiers = HotkeyBinding.ModCtrl | HotkeyBinding.ModAlt }.ToString());
        Assert.Equal("Боковая кнопка мыши «Вперёд»", new HotkeyBinding { Mouse = MouseButtons.XButton2 }.ToString());
    }
}
