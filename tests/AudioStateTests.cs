using Xunit;

namespace BtQuickConnect.Tests;

public class AudioStateTests
{
    private const string A = "4072181F92CA", B = "445ECD118884";

    private static List<BtDevice> Devices(bool aclA, bool aclB) => new()
    {
        new BtDevice(A, "OnePlus Buds 3", aclA, true),
        new BtDevice(B, "Razer Kraken", aclB, true),
    };

    [Fact]
    public void SoundGone_RadioStillUp_ShowsDisconnected()
    {
        // замер 20.10: звук пропадает через 0,34 с, радиосоединение держится ещё ~3 с
        var r = TrayApp.WithAudioState(Devices(aclA: true, aclB: true), A, audio: false)!;
        Assert.False(r.Single(d => d.Address == A).Connected);
        Assert.True(r.Single(d => d.Address == B).Connected);   // другие устройства не трогаем
    }

    [Fact]
    public void SoundUp_ShowsConnected() =>
        Assert.True(TrayApp.WithAudioState(Devices(true, false), A, audio: true)!.Single(d => d.Address == A).Connected);

    [Fact]
    public void NotAudioDevice_UsesRadio()
    {
        var src = Devices(aclA: true, aclB: false);
        Assert.Same(src, TrayApp.WithAudioState(src, A, audio: null));
    }

    [Fact]
    public void AddressCaseInsensitive() =>
        Assert.False(TrayApp.WithAudioState(Devices(true, true), A.ToLowerInvariant(), false)!.Single(d => d.Address == A).Connected);

    [Fact]
    public void BluetoothOff_StaysNull() => Assert.Null(TrayApp.WithAudioState(null, A, true));
}
