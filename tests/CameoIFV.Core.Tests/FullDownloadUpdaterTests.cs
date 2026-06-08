using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CameoIFV.Core.Update;
using Xunit;

namespace CameoIFV.Core.Tests;

/// <summary>
/// Behavioural tests for the full-download fallback: it must survive GitHub's intermittent 5xx /
/// dropped connections by retrying, and resume from bytes already on disk rather than restarting.
/// </summary>
public class FullDownloadUpdaterTests
{
    private static readonly Uri AssetUrl = new("https://example.test/asset.zip");

    private static UpdatePlan PlanFor(TempDir dir, long size) => new()
    {
        AssetUrl = AssetUrl,
        AssetSize = size,
        OutputZipPath = System.IO.Path.Combine(dir.Path, "out.zip.part"),
    };

    private static byte[] MakeContent(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i % 251);
        return b;
    }

    [Fact]
    public async Task Retries_TransientServerError_ThenSucceeds()
    {
        using var dir = new TempDir();
        var content = MakeContent(64 * 1024);
        var calls = 0;
        var handler = new ScriptedHandler(req =>
        {
            calls++;
            if (calls == 1)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return Full(content);
        });
        using var http = new HttpClient(handler);
        var updater = new FullDownloadUpdater(http);

        await updater.UpdateAsync(PlanFor(dir, content.Length), null, CancellationToken.None);

        Assert.Equal(2, calls); // failed once, retried once
        Assert.Equal(content, await File.ReadAllBytesAsync(PlanFor(dir, content.Length).OutputZipPath));
    }

    [Fact]
    public async Task Retries_On503_HonoringRetryAfter_ThenSucceeds()
    {
        using var dir = new TempDir();
        var content = MakeContent(32 * 1024);
        var calls = 0;
        var handler = new ScriptedHandler(req =>
        {
            calls++;
            if (calls == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return resp;
            }
            return Full(content);
        });
        using var http = new HttpClient(handler);
        var updater = new FullDownloadUpdater(http);

        await updater.UpdateAsync(PlanFor(dir, content.Length), null, CancellationToken.None);

        Assert.Equal(2, calls); // 503 (Retry-After: 0) then success
        Assert.Equal(content, await File.ReadAllBytesAsync(PlanFor(dir, content.Length).OutputZipPath));
    }

    [Fact]
    public async Task Resumes_FromBytesOnDisk_AfterTruncatedBody()
    {
        using var dir = new TempDir();
        var content = MakeContent(64 * 1024);
        var half = content.Length / 2;
        var calls = 0;
        var handler = new ScriptedHandler(req =>
        {
            calls++;
            if (calls == 1)
            {
                // Server claims the full length but delivers only the first half (a dropped connection).
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content[..half]),
                };
                resp.Content.Headers.ContentLength = content.Length;
                return resp;
            }

            // Retry must carry a Range header to resume from `half`.
            Assert.NotNull(req.Headers.Range);
            var from = (int)req.Headers.Range!.Ranges.First().From!.Value;
            Assert.Equal(half, from);

            var rest = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[from..]),
            };
            rest.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, content.Length - 1, content.Length);
            return rest;
        });
        using var http = new HttpClient(handler);
        var updater = new FullDownloadUpdater(http);

        await updater.UpdateAsync(PlanFor(dir, content.Length), null, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(content, await File.ReadAllBytesAsync(PlanFor(dir, content.Length).OutputZipPath));
    }

    [Fact]
    public async Task DoesNotRetry_WhenCancelled()
    {
        using var dir = new TempDir();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler);
        var updater = new FullDownloadUpdater(http);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => updater.UpdateAsync(PlanFor(dir, 1024), null, cts.Token));
    }

    private static HttpResponseMessage Full(byte[] content)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        resp.Content.Headers.ContentLength = content.Length;
        return resp;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }
}
