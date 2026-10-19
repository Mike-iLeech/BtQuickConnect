using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace BtQuickConnect.Tests;

public class UpdaterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("btqc-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Release(string tag, string assets) =>
        $$"""{"tag_name":"{{tag}}","html_url":"https://example/r","assets":[{{assets}}]}""";

    private static string Asset(string name, long size = 10, string? digest = "sha256:ABCDEF") =>
        $$"""{"name":"{{name}}","browser_download_url":"https://example/{{name}}","size":{{size}}{{(digest == null ? "" : $",\"digest\":\"{digest}\"")}}}""";

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("v2", "2.0.0.0")]
    [InlineData("release-1.0.5.7", "1.0.5.7")]
    public void ParseVersion_Normalizes(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), Updater.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    public void ParseVersion_NotAVersion(string tag) => Assert.Null(Updater.ParseVersion(tag));

    [Fact]
    public void ParseRelease_PicksOurAsset_AndDigest()
    {
        var r = Updater.ParseRelease(Release("v1.4.0",
            Asset("BtQuickConnect.exe.sha256") + "," + Asset("BtQuickConnect.exe", 290_000)));
        Assert.NotNull(r);
        Assert.Equal(new Version(1, 4, 0, 0), r!.Version);
        Assert.Equal("https://example/BtQuickConnect.exe", r.AssetUrl);
        Assert.Equal(290_000, r.AssetSize);
        Assert.Equal("abcdef", r.Sha256);
    }

    [Fact]
    public void ParseRelease_NoDigest_ShaIsNull() =>
        Assert.Null(Updater.ParseRelease(Release("v1.4.0", Asset("BtQuickConnect.exe", digest: null)))!.Sha256);

    [Fact]
    public void ParseRelease_NoExe_IsNull() =>
        Assert.Null(Updater.ParseRelease(Release("v1.4.0", Asset("Other.exe"))));

    [Fact]
    public void ParseRelease_BadTag_IsNull() =>
        Assert.Null(Updater.ParseRelease(Release("nightly", Asset("BtQuickConnect.exe"))));

    [Fact]
    public void IsNewer_ComparesWithCurrent()
    {
        var cur = Updater.Current;
        ReleaseInfo R(Version v) => new(v, "v" + v, "", "", 0, null);
        Assert.True(Updater.IsNewer(R(new Version(cur.Major, cur.Minor, cur.Build + 1, 0))));
        Assert.False(Updater.IsNewer(R(cur)));
        Assert.False(Updater.IsNewer(R(new Version(0, 0, 0, 1))));
    }

    // ---------- скачивание: размер и SHA-256 ----------

    private static (HttpListener, string) Serve(byte[] body)
    {
        for (int port = 49_500; ; port++)
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { l.Start(); }
            catch (HttpListenerException) { l.Close(); continue; }
            _ = Task.Run(async () =>
            {
                try
                {
                    while (l.IsListening)
                    {
                        var ctx = await l.GetContextAsync();
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                }
                catch { /* слушатель закрыт */ }
            });
            return (l, $"http://127.0.0.1:{port}/BtQuickConnect.exe");
        }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Download_GoodChecksum_ReturnsNewFile()
    {
        byte[] body = RandomNumberGenerator.GetBytes(100_000);
        var (l, url) = Serve(body);
        using var _ = l;
        string exe = Path.Combine(_dir, "BtQuickConnect.exe");
        var r = new ReleaseInfo(new Version(9, 0, 0, 0), "v9", "", url, body.Length, Sha(body));

        string file = await Updater.DownloadAsync(r, exe);

        Assert.Equal(exe + ".new", file);
        Assert.Equal(body, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Download_WrongChecksum_Throws_AndLeavesNothing()
    {
        byte[] body = RandomNumberGenerator.GetBytes(1000);
        var (l, url) = Serve(body);
        using var _ = l;
        string exe = Path.Combine(_dir, "BtQuickConnect.exe");
        var r = new ReleaseInfo(new Version(9, 0, 0, 0), "v9", "", url, body.Length, new string('0', 64));

        await Assert.ThrowsAsync<UpdateException>(() => Updater.DownloadAsync(r, exe));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public async Task Download_WrongSize_Throws()
    {
        byte[] body = RandomNumberGenerator.GetBytes(1000);
        var (l, url) = Serve(body);
        using var _ = l;
        string exe = Path.Combine(_dir, "BtQuickConnect.exe");
        var r = new ReleaseInfo(new Version(9, 0, 0, 0), "v9", "", url, body.Length + 1, Sha(body));

        await Assert.ThrowsAsync<UpdateException>(() => Updater.DownloadAsync(r, exe));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public async Task Download_NoChecksum_Refuses()
    {
        string exe = Path.Combine(_dir, "BtQuickConnect.exe");
        var r = new ReleaseInfo(new Version(9, 0, 0, 0), "v9", "", "http://127.0.0.1:1/x", 1, null);
        await Assert.ThrowsAsync<UpdateException>(() => Updater.DownloadAsync(r, exe));
    }

    // ---------- подмена EXE ----------

    [Fact]
    public void Install_SwapsFiles_AndStartsNewExe()
    {
        // «новая версия» — настоящий EXE, который сразу завершится
        string exe = Path.Combine(_dir, "BtQuickConnect.exe");
        File.WriteAllText(exe, "old");
        string fresh = exe + ".new";
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), fresh);
        byte[] freshBytes = File.ReadAllBytes(fresh);

        Updater.InstallAndRestart(fresh, exe);

        Assert.Equal(freshBytes, File.ReadAllBytes(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(fresh));
    }

    [Fact]
    public void Install_NewExeDoesNotStart_RollsBack()
    {
        string exe = Path.Combine(_dir, "BtQuickConnect.exe");
        File.WriteAllText(exe, "old");
        string fresh = exe + ".new";
        File.WriteAllText(fresh, "not a program");

        Assert.Throws<UpdateException>(() => Updater.InstallAndRestart(fresh, exe));

        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(fresh));
    }
}
