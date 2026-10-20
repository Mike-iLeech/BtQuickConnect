using Xunit;

namespace BtQuickConnect.Tests;

public class DisconnectStateTests
{
    private const string A = "4072181F92CA", B = "445ECD118884";
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0);
    private static DateTime S(double sec) => T0.AddSeconds(sec);

    [Fact]
    public void Idle_NothingActive() => Assert.False(new DisconnectState().Active(A, T0));

    [Fact]
    public void ProfilesStillDropping_PollSeesDisconnected_StaysActive()
    {
        // A2DP уже порван (опрос видит «не подключено»), а Hands-Free ещё рвётся — нажатие должно ждать
        var d = new DisconnectState();
        d.Begin(A, T0);
        d.Observe(A, connected: false, S(1));
        Assert.True(d.Active(A, S(1)));
    }

    [Fact]
    public void Finished_ThenPollSeesDisconnected_Clears()
    {
        var d = new DisconnectState();
        int op = d.Begin(A, T0);
        d.Finished(op, S(2), failed: false);
        Assert.True(d.Active(A, S(2)));            // огонёк ещё может быть зелёным
        d.Observe(A, connected: false, S(3));
        Assert.False(d.Active(A, S(3)));
    }

    [Fact]
    public void Finished_StillConnected_ClearsAfterSettleLimit()
    {
        var d = new DisconnectState();
        int op = d.Begin(A, T0);
        d.Finished(op, S(1), failed: false);
        d.Observe(A, connected: true, S(5));
        Assert.True(d.Active(A, S(5)));
        Assert.False(d.Active(A, S(1) + DisconnectState.SettleLimit + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void Failed_ClearsAtOnce()
    {
        var d = new DisconnectState();
        int op = d.Begin(A, T0);
        d.Finished(op, S(1), failed: true);
        Assert.False(d.Active(A, S(1)));
    }

    [Fact]
    public void DisconnectNeverReturns_DoesNotStickForever()
    {
        // драйвер завис или ответ потерялся — ожидание всё равно кончается
        var d = new DisconnectState();
        d.Begin(A, T0);
        var end = T0 + DisconnectState.InFlightLimit + DisconnectState.SettleLimit;
        Assert.True(d.Active(A, end - TimeSpan.FromSeconds(1)));
        Assert.False(d.Active(A, end));
        Assert.False(d.InFlight(T0 + DisconnectState.InFlightLimit));
    }

    [Fact]
    public void HungThenPollSeesDisconnected_ClearsAfterInFlightLimit()
    {
        var d = new DisconnectState();
        d.Begin(A, T0);
        var after = T0 + DisconnectState.InFlightLimit + TimeSpan.FromSeconds(1);
        d.Observe(A, connected: false, after);
        Assert.False(d.Active(A, after));
    }

    [Fact]
    public void OtherDevice_NotBlocked()
    {
        var d = new DisconnectState();
        d.Begin(A, T0);
        Assert.False(d.Active(B, S(1)));
        Assert.False(d.Active(null, S(1)));
        d.Observe(B, connected: false, S(1));       // чужой опрос не снимает ожидание A
        Assert.True(d.Active(A, S(1)));
    }

    [Fact]
    public void AddressCaseInsensitive()
    {
        var d = new DisconnectState();
        d.Begin(A, T0);
        Assert.True(d.Active(A.ToLowerInvariant(), S(1)));
    }

    [Fact]
    public void LateAnswerForOtherDevice_DoesNotTouchCurrent()
    {
        // отключали A, сразу выбрали B и отключаем его; ответ по A приходит позже
        var d = new DisconnectState();
        int opA = d.Begin(A, T0);
        d.Begin(B, S(1));
        d.Finished(opA, S(2), failed: true);          // ошибка по A не должна снять ожидание B
        Assert.True(d.InFlight(S(2)));
        d.Observe(B, connected: false, S(3));          // A2DP у B уже порван, Hands-Free ещё рвётся
        Assert.True(d.Active(B, S(3)));
    }

    [Fact]
    public void StaleAnswerAfterHang_IgnoredForReconnectedDevice()
    {
        // Disconnect завис дольше лимита, устройство подключили заново, старый ответ пришёл позже
        var d = new DisconnectState();
        int op1 = d.Begin(A, T0);
        var later = T0 + DisconnectState.InFlightLimit + TimeSpan.FromSeconds(1);
        d.Observe(A, connected: false, later);
        Assert.False(d.Active(A, later));
        int op2 = d.Begin(A, later.AddSeconds(5));     // снова отключаем
        d.Finished(op1, later.AddSeconds(6), failed: false);
        Assert.True(d.InFlight(later.AddSeconds(6)));  // старый ответ не оборвал новое отключение
        d.Finished(op2, later.AddSeconds(7), failed: true);
        Assert.False(d.Active(A, later.AddSeconds(7)));
    }

    [Fact]
    public void AnswerAfterLimit_DoesNotBlockReconnectedDevice()
    {
        // завис дольше лимита → опрос увидел отключение → пользователь подключил заново → старый ответ пришёл
        var d = new DisconnectState();
        int op = d.Begin(A, T0);
        var later = T0 + DisconnectState.InFlightLimit + TimeSpan.FromSeconds(1);
        d.Observe(A, connected: false, later);
        d.Observe(A, connected: true, later.AddSeconds(4));
        d.Finished(op, later.AddSeconds(5), failed: false);
        Assert.False(d.Active(A, later.AddSeconds(5)));
    }
}
