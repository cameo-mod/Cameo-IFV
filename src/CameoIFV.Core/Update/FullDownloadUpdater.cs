namespace CameoIFV.Core.Update;

/// <summary>
/// Fallback path: download the whole zip. Used on first install and for releases that ship no
/// .zsync (e.g. Combined Arms today). Streams to disk with progress and cancellation.
/// </summary>
public sealed class FullDownloadUpdater : IUpdater
{
    private readonly HttpClient _http;

    public FullDownloadUpdater(HttpClient http) => _http = http;

    public UpdateMode Mode => UpdateMode.FullDownload;

    public async Task UpdateAsync(UpdatePlan plan, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(plan.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Prefer the server's declared length; fall back to the catalog's expected size.
        var declared = response.Content.Headers.ContentLength;
        var total = declared ?? plan.AssetSize;
        var expected = declared ?? (plan.AssetSize > 0 ? plan.AssetSize : (long?)null);

        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputZipPath)!);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var dest = new FileStream(plan.OutputZipPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[1 << 16];
        long transferred = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            transferred += read;
            progress?.Report(new UpdateProgress(transferred, total));
        }

        // Guard against a silently truncated download (dropped connection with no exception):
        // leaving a short .part that later masquerades as a valid install.
        if (expected is { } e && transferred != e)
            throw new IOException($"Download truncated: got {transferred} bytes, expected {e}.");
    }
}
