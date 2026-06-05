using System.Net.Http;
using CameoIFV.Core.Update;
using Xunit;

namespace CameoIFV.Core.Tests;

/// <summary>
/// Network-touching smoke test against a real, stable Cameo release asset. Verifies that our
/// IRangeDownloader honours byte ranges through GitHub's 302 -> CDN redirect. Skipped offline.
/// </summary>
public class GitHubRangeDownloaderTests
{
    // A published, immutable release asset (playtest tag won't move).
    private static readonly Uri AssetUrl = new(
        "https://github.com/cameo-mod/Cameo-mod/releases/download/playtest-20260531/" +
        "CameoMod-playtest-20260531-x64-winportable.zip");

    private static HttpClient NewClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        http.DefaultRequestHeaders.Add("User-Agent", "Cameo-IFV-Tests/0.1");
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    private static bool Online()
    {
        try
        {
            using var http = NewClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, AssetUrl);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var resp = http.SendAsync(req).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    [SkippableFact]
    [Trait("Category", "Network")]
    public void DownloadRange_ReturnsExactlyRequestedBytes_StartsWithZipMagic()
    {
        Skip.IfNot(Online(), "No network / asset unreachable.");

        using var http = NewClient();
        var dl = new GitHubRangeDownloader(http, AssetUrl);

        // First 4 bytes of any zip are the local-file-header magic "PK\x03\x04".
        using var head = dl.DownloadRange(0, 4);
        using var ms = new MemoryStream();
        head.CopyTo(ms);
        var bytes = ms.ToArray();

        Assert.Equal(4, bytes.Length);
        Assert.Equal(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, bytes);

        // A 1 KiB mid-file range must come back as exactly 1024 bytes.
        using var mid = dl.DownloadRange(100_000, 100_000 + 1024);
        using var ms2 = new MemoryStream();
        mid.CopyTo(ms2);
        Assert.Equal(1024, ms2.ToArray().Length);
    }
}
