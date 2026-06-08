using System.Net;

namespace CameoIFV.Core.Update;

/// <summary>
/// A retryable HTTP failure (throttling / transient server error). Carries the server's requested
/// back-off from a <c>Retry-After</c> header, when present, so the retry loops wait exactly as long
/// as the CDN asks instead of guessing — the well-behaved-client behaviour that keeps us off abuse
/// heuristics under load.
/// </summary>
public sealed class TransientHttpException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public TransientHttpException(string message, TimeSpan? retryAfter = null) : base(message)
        => RetryAfter = retryAfter;
}

internal static class RetryPolicy
{
    /// <summary>Statuses worth retrying with back-off: request timeout, rate limit, and 5xx.</summary>
    public static bool IsRetryableStatus(HttpStatusCode code)
        => code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)code >= 500;

    /// <summary>Server-requested back-off from Retry-After (delta seconds or an HTTP date), or null.</summary>
    public static TimeSpan? RetryAfterDelay(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
            return null;

        if (header.Delta is { } delta)
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;

        if (header.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>Honour the server's delay but never stall absurdly long on one request.</summary>
    public static TimeSpan Clamp(TimeSpan delay, TimeSpan max)
        => delay < TimeSpan.Zero ? TimeSpan.Zero : delay > max ? max : delay;
}
