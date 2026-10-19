using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace BtQuickConnect;

internal sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string AssetUrl, long AssetSize, string? Sha256);

internal sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Автообновление из GitHub Releases.
/// Работающий EXE нельзя перезаписать, но можно переименовать: текущий уходит в .old,
/// новый встаёт на его место и запускается, а .old удаляет уже новая версия при старте.
/// </summary>
internal static class Updater
{
    public const string Repo = "Mike-iLeech/BtQuickConnect";
    public const string AssetName = "BtQuickConnect.exe";
    public const string UpdatedFlag = "--updated";
    private const string ApiLatest = $"https://api.github.com/repos/{Repo}/releases/latest";

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BtQuickConnect", Current.ToString()));
        return c;
    }

    /// <summary>Обновлять на месте можно только собранный EXE, а не запуск через dotnet.exe.</summary>
    public static string? ExePath
    {
        get
        {
            string? p = Environment.ProcessPath;
            return p != null && Path.GetFileName(p).Equals(AssetName, StringComparison.OrdinalIgnoreCase) ? p : null;
        }
    }

    /// <summary>'v1.2' / '1.2.0.0' → 1.2.0.0; хвостовые нули не важны. null — тег не версия.</summary>
    public static Version? ParseVersion(string? text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"\d+(\.\d+){0,3}");
        return m.Success && Version.TryParse(m.Value.Contains('.') ? m.Value : m.Value + ".0", out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool IsNewer(ReleaseInfo r) => r.Version > Current;

    /// <summary>Разбирает ответ /releases/latest. null — в релизе нет нашего EXE или тег не версия.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = ParseVersion(tag);
        if (version == null || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var a in assets.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var n) || !AssetName.Equals(n.GetString(), StringComparison.OrdinalIgnoreCase))
                continue;
            string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) return null;
            long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
            string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..].ToLowerInvariant() : null;
            string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            return new ReleaseInfo(version, tag, page, url, size, sha);
        }
        return null;
    }

    public static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiLatest);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        { throw new UpdateException("Нет связи с GitHub.", e); }

        using (resp)
        {
            if ((int)resp.StatusCode == 404) return null;            // ещё нет ни одного релиза
            if ((int)resp.StatusCode is 403 or 429)
                throw new UpdateException("GitHub временно ограничил число запросов.");
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"GitHub ответил ошибкой {(int)resp.StatusCode}.");
            try { return ParseRelease(await resp.Content.ReadAsStringAsync(ct)); }
            catch (JsonException e) { throw new UpdateException("Не удалось разобрать ответ GitHub.", e); }
        }
    }

    /// <summary>Качает EXE рядом с текущим (.new) и сверяет размер и SHA-256. Возвращает путь к файлу.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo r, string exePath, CancellationToken ct = default)
    {
        if (r.Sha256 == null)
            throw new UpdateException("У релиза нет контрольной суммы — обновление не установлено.");

        string tmp = exePath + ".new";
        try
        {
            using (var resp = await Http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct);
            }

            var info = new FileInfo(tmp);
            if (r.AssetSize > 0 && info.Length != r.AssetSize)
                throw new UpdateException("Файл обновления скачался не полностью.");

            string actual;
            await using (var fs = File.OpenRead(tmp))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (actual != r.Sha256)
                throw new UpdateException("Контрольная сумма обновления не совпала — файл отброшен.");
            return tmp;
        }
        catch (Exception e)
        {
            TryDelete(tmp);
            if (e is UpdateException) throw;
            if (e is UnauthorizedAccessException)
                throw new UpdateException("Нет прав на запись в папку программы — переместите её, например, в Документы.", e);
            throw new UpdateException("Не удалось скачать обновление: " + e.Message, e);
        }
    }

    /// <summary>Подменяет EXE и запускает новую версию; после успешного возврата вызывающий должен выйти.</summary>
    public static void InstallAndRestart(string newFile, string exePath)
    {
        string old = exePath + ".old";
        TryDelete(old);
        try
        {
            File.Move(exePath, old);
        }
        catch (Exception e)
        {
            TryDelete(newFile);
            throw new UpdateException("Не удалось заменить программу: " + e.Message, e);
        }
        try
        {
            File.Move(newFile, exePath);
        }
        catch (Exception e)
        {
            try { File.Move(old, exePath); } catch { /* откат не удался — .old остаётся рядом */ }
            TryDelete(newFile);
            throw new UpdateException("Не удалось заменить программу: " + e.Message, e);
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath, $"{UpdatedFlag} {Current}") { UseShellExecute = false });
        }
        catch (Exception e)
        {
            // новая версия не стартует — возвращаем старую на место
            try { File.Move(exePath, newFile, true); File.Move(old, exePath); } catch { }
            TryDelete(newFile);
            throw new UpdateException("Новая версия не запустилась: " + e.Message, e);
        }
    }

    /// <summary>Удаляет .old/.new, оставшиеся от прошлого обновления.</summary>
    public static void Cleanup()
    {
        if (ExePath is not { } exe) return;
        for (int i = 0; i < 10 && File.Exists(exe + ".old"); i++)
        {
            TryDelete(exe + ".old");
            if (File.Exists(exe + ".old")) Thread.Sleep(300);   // старый процесс ещё выходит
        }
        TryDelete(exe + ".new");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
