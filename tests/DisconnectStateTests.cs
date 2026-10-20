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
        d.Begin(A, T0);
        d.Finished(S(2), failed: false);
        Assert.True(d.Active(A, S(2)));            // огонёк ещё может быть зелёным
        d.Observe(A, connected: false, S(3));
        Assert.False(d.Active(A, S(3)));
    }

    [Fact]
    public void Finished_StillConnected_ClearsAfterSettleLimit()
    {
        var d = new DisconnectState();
        d.Begin(A, T0);
        d.Finished(S(1), failed: false);
        d.Observe(A, connected: true, S(5));
        Assert.True(d.Active(A, S(5)));
        Assert.False(d.Active(A, S(1) + DisconnectState.SettleLimit + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void Failed_ClearsAtOnce()
    {
        var d = new DisconnectState();
        d.Begin(A, T0);
        d.Finished(S(1), failed: true);
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
}
