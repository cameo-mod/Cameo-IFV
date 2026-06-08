using System.Net;
using System.Net.Http.Headers;
using zsyncnet;

namespace CameoIFV.Core.Update;

/// <summary>
/// Our own <see cref="IRangeDownloader"/> for zsyncnet, so the zsync algorithm is reused but the
/// transport is ours: a shared <see cref="HttpClient"/> that follows GitHub's 302 -> CDN redirect,
/// sends our User-Agent, and issues HTTP range requests (verified to return 206 for release assets).
///
/// A real incremental update fans out into thousands of small ranges, and GitHub's asset URL
/// 302-redirects to a signed CDN URL on every hit. We resolve that signed URL once and aim every
/// subsequent range straight at it, re-resolving only if it expires. The async entry point
/// (<see cref="DownloadRangeAsync"/>) lets <see cref="ZsyncUpdater"/> fetch ranges in parallel —
/// sequential range fetching is latency-bound (~30 ms/round-trip) and slower than a full download.
/// </summary>
public sealed class GitHubRangeDownloader : IRangeDownloader
{
    private readonly HttpClient _http;
    private readonly Uri _assetUrl;

    // The post-redirect signed CDN URL, cached after the first request so later ranges skip the 302.
    // Volatile: parallel prefetch reads/writes this from many threads.
    private volatile Uri? _resolvedUrl;

    public GitHubRangeDownloader(HttpClient http, Uri assetUrl)
    {
        _http = http;
        _assetUrl = assetUrl;
    }

    /// <summary>Synchronous entry point for zsyncnet. Prefer <see cref="DownloadRangeAsync"/> off the hot path.</summary>
    public Stream DownloadRange(long from, long to)
        => SendRangeAsync(from, to, allowReResolve: true, CancellationToken.None).GetAwaiter().GetResult();

    public Stream Download()
        => DownloadFullAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Download bytes [from, to) — <paramref name="to"/> is exclusive, matching zsyncnet.</summary>
    public Task<Stream> DownloadRangeAsync(long from, long to, CancellationToken cancellationToken)
        => SendRangeAsync(from, to, allowReResolve: true, cancellationToken);

    private async Task<Stream> SendRangeAsync(long from, long to, bool allowReResolve, CancellationToken cancellationToken)
    {
        // Prefer the cached signed URL (no redirect); fall back to the asset URL to (re)resolve it.
        var target = _resolvedUrl ?? _assetUrl;
        var request = new HttpRequestMessage(HttpMethod.Get, target);
        // HTTP Range is inclusive on both ends; zsyncnet's 'to' is exclusive.
        request.Headers.Range = new RangeHeaderValue(from, to - 1);

        var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // Signed CDN URLs are time-limited: a cached one can come back 403/410 once it expires.
        // Drop it and retry once against the asset URL, which re-issues a fresh redirect.
        if (allowReResolve && !ReferenceEquals(target, _assetUrl)
            && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            response.Dispose();
            request.Dispose();
            _resolvedUrl = null;
            return await SendRangeAsync(from, to, allowReResolve: false, cancellationToken).ConfigureAwait(false);
        }

        return Finish(request, response, expectPartial: true);
    }

    private async Task<Stream> DownloadFullAsync(CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _assetUrl);
        var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        return Finish(request, response, expectPartial: false);
    }

    private Stream Finish(HttpRequestMessage request, HttpResponseMessage response, bool expectPartial)
    {
        try
        {
            // Throttling / transient server errors carry a Retry-After we want to honour; surface it
            // as a TransientHttpException so the (parallel) retry loop backs off as the CDN asks.
            if (RetryPolicy.IsRetryableStatus(response.StatusCode))
                throw new TransientHttpException(
                    $"Range request to {_assetUrl} returned {(int)response.StatusCode} {response.StatusCode}.",
                    RetryPolicy.RetryAfterDelay(response));

            response.EnsureSuccessStatusCode();

            // A 200 OK to a Range request means a proxy/AV/CDN ignored the header and is handing us
            // the WHOLE asset, not the slice zsyncnet asked for. Feeding that to the patcher would
            // either silently re-download ~900 MB per "range" or mis-assemble the archive. Fail loud.
            if (expectPartial && response.StatusCode != HttpStatusCode.PartialContent)
                throw new HttpRequestException(
                    $"Range request to {_assetUrl} returned {(int)response.StatusCode} {response.StatusCode}, " +
                    "expected 206 Partial Content — the server or an intermediary is not honouring HTTP Range.");

            // Cache the URL the redirect actually landed on, so the next range skips the 302 hop.
            var landed = response.RequestMessage?.RequestUri;
            if (landed is not null && !ReferenceEquals(landed, _assetUrl) && landed != _assetUrl)
                _resolvedUrl = landed;

            var content = response.Content.ReadAsStream();
            // Hand back a stream that owns the response: disposing it (zsyncnet does) frees the
            // HttpResponseMessage too, so we don't leak responses/connections across many ranges.
            return new ResponseOwningStream(content, response, request);
        }
        catch
        {
            response.Dispose();
            request.Dispose();
            throw;
        }
    }

    /// <summary>Delegating stream that disposes its owning response + request when disposed.</summary>
    private sealed class ResponseOwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private readonly HttpRequestMessage _request;

        public ResponseOwningStream(Stream inner, HttpResponseMessage response, HttpRequestMessage request)
        {
            _inner = inner;
            _response = response;
            _request = request;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
                _request.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
