using System.Net;
using System.Net.Http.Headers;

namespace CameoIFV.Core.Update;

/// <summary>
/// Fallback path: download the whole zip. Used on first install and for releases that ship no
/// .zsync (e.g. Combined Arms, and Cameo playtests without a sidecar). Streams to disk with
/// progress and cancellation.
///
/// GitHub's release CDN intermittently returns 5xx / drops connections mid-transfer, which on a
/// ~1 GB asset would otherwise fail the whole install. So transient failures are retried with
/// backoff, and each retry RESUMES via an HTTP Range request from the bytes already on disk
/// (the asset returns 206), rather than restarting from zero.
/// </summary>
public sealed class FullDownloadUpdater : IUpdater
{
    private const int MaxAttempts = 5;

    private readonly HttpClient _http;

    public FullDownloadUpdater(HttpClient http) => _http = http;

    public UpdateMode Mode => UpdateMode.FullDownload;

    public async Task UpdateAsync(UpdatePlan plan, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputZipPath)!);

        // Start clean: never resume onto a partial left by an earlier, unrelated run.
        if (File.Exists(plan.OutputZipPath))
            File.Delete(plan.OutputZipPath);

        long? expected = plan.AssetSize > 0 ? plan.AssetSize : null;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Resume from whatever this session has already written.
            var resumeFrom = File.Exists(plan.OutputZipPath) ? new FileInfo(plan.OutputZipPath).Length : 0;

            try
            {
                await DownloadAsync(plan, resumeFrom, expected, progress, cancellationToken);

                // Guard against a silently truncated download (dropped connection with no exception):
                // a short .part must not masquerade as a valid install.
                var got = new FileInfo(plan.OutputZipPath).Length;
                if (expected is { } e && got != e)
                    throw new IOException($"Download truncated: got {got} bytes, expected {e}.");

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // user cancelled — don't retry.
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                // Honour a server Retry-After when present; otherwise exponential backoff (2s, 4s, 8s,
                // 16s, capped at 30s). The next iteration resumes from the bytes already on disk.
                var delay = ex is TransientHttpException { RetryAfter: { } retryAfter }
                    ? RetryPolicy.Clamp(retryAfter, TimeSpan.FromSeconds(60))
                    : TimeSpan.FromSeconds(Math.Min(30, 1 << attempt));
                var onDisk = File.Exists(plan.OutputZipPath) ? new FileInfo(plan.OutputZipPath).Length : 0;
                progress?.Report(new UpdateProgress(onDisk, expected ?? 0,
                    $"full download: attempt {attempt} failed ({ex.GetType().Name}: {ex.Message}); "
                    + $"retrying in {delay.TotalSeconds:F0}s, resuming from {FormatBytes(onDisk)}"));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task DownloadAsync(
        UpdatePlan plan, long resumeFrom, long? expected, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, plan.AssetUrl);
        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null); // bytes=N-

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Throttling / transient server errors carry a Retry-After we want to honour; surface it as a
        // TransientHttpException so the retry loop backs off as the server asks. Other 4xx fall through
        // to EnsureSuccessStatusCode and fail fast (not retried).
        if (RetryPolicy.IsRetryableStatus(response.StatusCode))
            throw new TransientHttpException(
                $"Download of {plan.AssetUrl} returned {(int)response.StatusCode} {response.StatusCode}.",
                RetryPolicy.RetryAfterDelay(response));

        response.EnsureSuccessStatusCode();

        // If we asked to resume but the server ignored Range and sent the whole file (200), restart
        // from byte 0 so we don't append the full body onto our partial.
        var serverResumed = response.StatusCode == HttpStatusCode.PartialContent;
        if (resumeFrom > 0 && !serverResumed)
            resumeFrom = 0;

        // Total size for the progress fraction: catalog size, else 206 Content-Range total, else 200 length.
        var total = expected
            ?? (serverResumed ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength);

        var mode = resumeFrom > 0 ? FileMode.Append : FileMode.Create;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var dest = new FileStream(plan.OutputZipPath, mode, FileAccess.Write, FileShare.None);

        var buffer = new byte[1 << 16];
        var transferred = resumeFrom;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            transferred += read;
            if (total is { } t)
                progress?.Report(new UpdateProgress(transferred, t));
        }
    }

    private static string FormatBytes(long n)
    {
        const double kib = 1024, mib = kib * 1024, gib = mib * 1024;
        return n switch
        {
            >= (long)gib => $"{n / gib:F2} GB",
            >= (long)mib => $"{n / mib:F1} MB",
            >= (long)kib => $"{n / kib:F0} KB",
            _ => $"{n} B",
        };
    }

    /// <summary>Failures worth retrying: server 5xx/408/429 and network-level drops mid-transfer.</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        TransientHttpException => true, // throttle / 5xx surfaced with the server's Retry-After
        // EnsureSuccessStatusCode sets StatusCode; a null code means a network-level failure (no response).
        HttpRequestException h => h.StatusCode is null
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or >= HttpStatusCode.InternalServerError,
        IOException => true,           // connection dropped / silent truncation
        TaskCanceledException => true, // HttpClient.Timeout (user cancellation is filtered earlier)
        _ => false,
    };
}
