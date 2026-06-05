using zsyncnet;

namespace CameoIFV.Core.Update;

/// <summary>
/// Our own <see cref="IRangeDownloader"/> for zsyncnet, so the zsync algorithm is reused but the
/// transport is ours: a shared <see cref="HttpClient"/> that follows GitHub's 302 -> CDN redirect,
/// sends our User-Agent, and issues HTTP range requests (verified to return 206 for release assets).
/// </summary>
public sealed class GitHubRangeDownloader : IRangeDownloader
{
    private readonly HttpClient _http;
    private readonly Uri _assetUrl;

    public GitHubRangeDownloader(HttpClient http, Uri assetUrl)
    {
        _http = http;
        _assetUrl = assetUrl;
    }

    /// <summary>Download bytes [from, to) — <paramref name="to"/> is exclusive, matching zsyncnet.</summary>
    public Stream DownloadRange(long from, long to)
    {
        // Ownership of the request transfers to Send (disposed via the returned stream).
        var request = new HttpRequestMessage(HttpMethod.Get, _assetUrl);
        // HTTP Range is inclusive on both ends; zsyncnet's 'to' is exclusive.
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to - 1);
        return Send(request);
    }

    public Stream Download()
        => Send(new HttpRequestMessage(HttpMethod.Get, _assetUrl));

    private Stream Send(HttpRequestMessage request)
    {
        // zsyncnet calls these synchronously from a worker thread; block to honour the Stream contract.
        var response = _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter().GetResult();
        try
        {
            response.EnsureSuccessStatusCode();
            var content = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
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
