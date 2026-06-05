using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using CameoIFV.Core.Config;
using CameoIFV.Core.Github;
using CameoIFV.Core.Model;
using Xunit;

namespace CameoIFV.Core.Tests;

public class ETagStoreTests
{
    [Fact]
    public void SaveThenReload_RoundTripsETagAndBody()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ifv-etag-{Guid.NewGuid():N}.json");
        try
        {
            const string url = "https://api.github.com/repos/x/y/releases";
            new ETagStore(file).Save(url, "\"abc123\"", "[{\"tag_name\":\"v1\"}]");

            var reloaded = new ETagStore(file); // fresh instance reads from disk
            Assert.Equal("\"abc123\"", reloaded.GetETag(url));
            Assert.Equal("[{\"tag_name\":\"v1\"}]", reloaded.GetBody(url));
            Assert.Null(reloaded.GetETag("https://other"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class ReleaseProviderTests
{
    /// <summary>Serves a canned releases JSON with a 200 + ETag so selection logic is testable offline.</summary>
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly string _json;
        public CannedHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            };
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"canned\"");
            return Task.FromResult(resp);
        }
    }

    private static ReleaseSource WindowsSource(string? zsyncSuffix) => new()
    {
        Channel = ReleaseChannel.Dev,
        Repository = "cameo-mod/Cameo-mod",
        Assets = new()
        {
            ["windows"] = new AssetFilter
            {
                AssetSuffix = "-x64-winportable.zip",
                ZsyncSuffix = zsyncSuffix,
            },
        },
    };

    private const string ReleasesJson = """
    [
      {
        "tag_name": "playtest-20260601", "name": "Playtest June 1", "published_at": "2026-06-01T00:00:00Z",
        "prerelease": true, "draft": false,
        "assets": [
          { "id": 1, "name": "CameoMod-playtest-20260601-x64-winportable.zip", "size": 1000, "updated_at": "2026-06-01T00:00:00Z", "browser_download_url": "https://example/zip" },
          { "id": 2, "name": "CameoMod-playtest-20260601-x64-winportable.zip.zsync", "size": 50, "updated_at": "2026-06-01T00:00:00Z", "browser_download_url": "https://example/zsync" }
        ]
      },
      {
        "tag_name": "playtest-20260531", "name": "Old", "published_at": "2026-05-31T00:00:00Z",
        "prerelease": true, "draft": false,
        "assets": [
          { "id": 3, "name": "CameoMod-playtest-20260531-x64-winportable.zip", "size": 900, "updated_at": "2026-05-31T00:00:00Z", "browser_download_url": "https://example/old-zip" }
        ]
      },
      {
        "tag_name": "draft-x", "name": "Draft", "published_at": "2026-06-02T00:00:00Z",
        "prerelease": true, "draft": true, "assets": []
      }
    ]
    """;

    private static ETagStore TempEtags() => new(Path.Combine(Path.GetTempPath(), $"ifv-{Guid.NewGuid():N}.json"));

    [Fact]
    public async Task SelectsWinportableAsset_AndZsyncSidecar_NewestFirst_SkipsDraft()
    {
        var http = new HttpClient(new CannedHandler(ReleasesJson));
        var provider = new GitHubReleaseProvider(http, TempEtags());

        var releases = await provider.ListAsync(WindowsSource(zsyncSuffix: "-x64-winportable.zip.zsync"), "windows", default);

        // Two releases (draft dropped), newest first.
        Assert.Equal(new[] { "playtest-20260601", "playtest-20260531" }, releases.Select(r => r.TagName));

        var latest = releases[0];
        Assert.Equal("https://example/zip", latest.AssetUrl.ToString());
        Assert.Equal(1000, latest.AssetSize);
        Assert.Equal("https://example/zsync", latest.ZsyncUrl?.ToString());
        Assert.True(latest.SupportsIncrementalUpdate);

        // Older release has no sidecar -> full-download path.
        Assert.Null(releases[1].ZsyncUrl);
        Assert.False(releases[1].SupportsIncrementalUpdate);
    }

    [Fact]
    public async Task IgnoresZsync_WhenFilterHasNoZsyncSuffix()
    {
        var http = new HttpClient(new CannedHandler(ReleasesJson));
        var provider = new GitHubReleaseProvider(http, TempEtags());

        var releases = await provider.ListAsync(WindowsSource(zsyncSuffix: null), "windows", default);

        Assert.All(releases, r => Assert.Null(r.ZsyncUrl));
    }

    /// <summary>200 + ETag on the first call; 304 (no body) when If-None-Match is echoed back.</summary>
    private sealed class ConditionalHandler : HttpMessageHandler
    {
        private readonly string _json;
        public int Calls { get; private set; }
        public ConditionalHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Headers.IfNoneMatch.Any())
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            };
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task SecondCall_Sends304_AndReusesCachedBody()
    {
        var handler = new ConditionalHandler(ReleasesJson);
        var http = new HttpClient(handler);
        var etags = TempEtags();
        var source = WindowsSource(zsyncSuffix: "-x64-winportable.zip.zsync");

        // First call primes the cache (200 + ETag).
        var first = await new GitHubReleaseProvider(http, etags).ListAsync(source, "windows", default);
        // Second call sends If-None-Match -> 304 -> must still yield the same releases from cache.
        var second = await new GitHubReleaseProvider(http, etags).ListAsync(source, "windows", default);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(first.Select(r => r.TagName), second.Select(r => r.TagName));
        Assert.Equal("playtest-20260601", second[0].TagName);
    }

    [Fact]
    public async Task UnsupportedPlatform_ReturnsEmpty()
    {
        var http = new HttpClient(new CannedHandler(ReleasesJson));
        var provider = new GitHubReleaseProvider(http, TempEtags());

        var releases = await provider.ListAsync(WindowsSource(zsyncSuffix: null), "linux", default);

        Assert.Empty(releases);
    }
}
