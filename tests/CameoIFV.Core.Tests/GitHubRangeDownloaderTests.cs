using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public void DownloadRange_Throws_WhenServerIgnoresRangeAndReturns200()
    {
        // A proxy/AV/CDN that ignores Range hands back the whole asset as 200 OK. Feeding that to
        // the patcher would corrupt the assembly, so the downloader must reject it loudly.
        var handler = new StubHandler((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[4096]),
            };
            return resp;
        });
        using var http = new HttpClient(handler);
        var dl = new GitHubRangeDownloader(http, AssetUrl);

        var ex = Assert.Throws<HttpRequestException>(() => dl.DownloadRange(0, 4096));
        Assert.Contains("206", ex.Message);
    }

    [Fact]
    public void DownloadRange_ReusesResolvedCdnUrl_AfterFirstRedirect()
    {
        // Simulate GitHub's 302 -> signed CDN URL by reporting the "landed" URL on the response.
        // The first range resolves it; every later range should target the signed URL directly,
        // skipping the redirect hop.
        var signed = new Uri("https://objects.example.test/signed-blob?token=abc");
        var targets = new List<Uri>();
        var handler = new StubHandler((req, _) =>
        {
            targets.Add(req.RequestUri!);
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[16]),
                // Pretend auto-redirect followed the 302 and landed on the signed URL.
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, signed),
            };
            return resp;
        });
        using var http = new HttpClient(handler);
        var dl = new GitHubRangeDownloader(http, AssetUrl);

        dl.DownloadRange(0, 16).Dispose();
        dl.DownloadRange(16, 32).Dispose();
        dl.DownloadRange(32, 48).Dispose();

        Assert.Equal(AssetUrl, targets[0]);     // first hop resolves via the asset URL
        Assert.Equal(signed, targets[1]);       // subsequent hops reuse the signed URL
        Assert.Equal(signed, targets[2]);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request, cancellationToken));
    }
}
