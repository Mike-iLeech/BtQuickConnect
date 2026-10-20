using Xunit;

namespace BtQuickConnect.Tests;

public class ProfileTimingTests
{
    private const string A = "4072181F92CA", B = "445ECD118884";
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0);
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(0.3);

    // ---------- зелёный — когда подняты все профили ----------

    [Fact]
    public void HandsFreeUp_StereoNot_KeepsWaiting() =>
        Assert.False(TrayApp.ConnectDone(new AudioState(true, AllUp: false), Short));

    [Fact]
    public void AllProfilesUp_Done() =>
        Assert.True(TrayApp.ConnectDone(new AudioState(true, AllUp: true), Short));

    [Fact]
    public void ProfileNeverComes_DoneAfterWait() =>
        Assert.True(TrayApp.ConnectDone(new AudioState(true, AllUp: false), TrayApp.AllProfilesWait));

    [Fact]
    public void NoAudioInfo_DoneByRadio()
    {
        Assert.True(TrayApp.ConnectDone(null, Short));                          // не аудиоустройство
        Assert.True(TrayApp.ConnectDone(new AudioState(null, true), Short));    // выключены в панели звука
    }

    // состояния MMDevice: 1 ACTIVE, 2 DISABLED, 4 NOTPRESENT, 8 UNPLUGGED
    [Theory]
    [InlineData(new[] { 1, 1, 1 }, true)]
    [InlineData(new[] { 1, 8, 1 }, false)]     // замер 20.10: Hands-Free поднят, стерео ещё нет
    [InlineData(new[] { 1, 2 }, true)]         // выключенное в панели звука не ждём
    [InlineData(new[] { 1, 4 }, true)]         // старая запись не ждём
    public void AllUp(int[] states, bool expected) => Assert.Equal(expected, AudioEndpoints.AllUp(states));

    // ---------- оранжевый сразу после успешного отключения ----------

    [Theory]
    [InlineData(true, null, true, true)]     // звук не понять, отключение прошло — сразу «отключено»
    [InlineData(true, null, false, false)]   // отключения не было — радиосоединение главнее
    [InlineData(true, true, true, false)]    // звук есть (переподключилось само) — не прячем
    [InlineData(false, null, true, false)]   // и так отключено
    public void ShowAsDisconnected(bool connected, bool? audio, bool settling, bool expected) =>
        Assert.Equal(expected, TrayApp.ShowAsDisconnected(connected, audio, settling));

    // ---------- цвет огонька целиком (ошибка 20.10: отключённое устройство горело зелёным) ----------

    private static BtDevice Dev(bool connected) => new(A, "OnePlus Buds 3", connected, true);

    [Theory]
    [InlineData(false, null, false, LedState.Orange)]   // отключено, звук не понять — оранжевый
    [InlineData(false, false, false, LedState.Orange)]  // отключено, звука нет
    [InlineData(false, null, true, LedState.Orange)]
    [InlineData(false, false, true, LedState.Orange)]
    [InlineData(true, true, false, LedState.Green)]
    [InlineData(true, null, false, LedState.Green)]     // неаудио/выключено в панели — по радио
    [InlineData(true, true, true, LedState.Green)]      // звук вернулся сам — не прячем
    [InlineData(true, null, true, LedState.Orange)]     // успешно отключили, радио ещё держится
    public void LedFor_Selected(bool connected, bool? audio, bool settling, object expected) =>
        Assert.Equal((LedState)expected, TrayApp.LedFor(Dev(connected), audio, settling));

    [Fact]
    public void LedFor_NothingSelected_Red() => Assert.Equal(LedState.Red, TrayApp.LedFor(null, null, false));

    [Fact]
    public void Settling_OnlyAfterSuccess()
    {
        var d = new DisconnectState();
        int op = d.Begin(A, T0);
        Assert.False(d.Settling(A, T0.AddSeconds(0.1)));         // ещё идёт
        d.Finished(op, T0.AddSeconds(0.2), failed: false);
        Assert.True(d.Settling(A, T0.AddSeconds(0.3)));
        Assert.False(d.Settling(B, T0.AddSeconds(0.3)));         // только своё устройство
        Assert.False(d.Settling(A, T0.AddSeconds(0.2) + DisconnectState.SettleLimit));   // срок вышел — верим радио
    }

    [Fact]
    public void Settling_NotAfterFailure()
    {
        var d = new DisconnectState();
        int op = d.Begin(A, T0);
        d.Finished(op, T0.AddSeconds(0.2), failed: true);
        Assert.False(d.Settling(A, T0.AddSeconds(0.3)));
    }

    [Fact]
    public void Settling_NotAfterLateAnswer()
    {
        var d = new DisconnectState();
        int op = d.Begin(A, T0);
        var late = T0 + DisconnectState.InFlightLimit + TimeSpan.FromSeconds(1);
        d.Finished(op, late, failed: false);
        Assert.False(d.Settling(A, late));
    }

    [Fact]
    public void Settling_NotClearedWhileRadioStillUp()
    {
        // опрос передаёт настоящее состояние (радио ещё держится) — показанный оранжевый не снимает ожидание
        var d = new DisconnectState();
        int op = d.Begin(A, T0);
        d.Finished(op, T0.AddSeconds(0.2), failed: false);
        d.Observe(A, connected: true, T0.AddSeconds(1));
        Assert.True(d.Settling(A, T0.AddSeconds(1)));
        Assert.True(d.Active(A, T0.AddSeconds(1)));
    }

    [Fact]
    public void Settling_EndsWhenRadioDrops()
    {
        var d = new DisconnectState();
        int op = d.Begin(A, T0);
        d.Finished(op, T0.AddSeconds(0.2), failed: false);
        d.Observe(A, connected: false, T0.AddSeconds(3.4));
        Assert.False(d.Settling(A, T0.AddSeconds(3.5)));
    }
}
