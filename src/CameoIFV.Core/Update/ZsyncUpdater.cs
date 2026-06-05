using zsyncnet;

namespace CameoIFV.Core.Update;

/// <summary>
/// Incremental path: fetch the .zsync control file, diff against the previously downloaded zip
/// (the seed), and range-fetch only the changed blocks via <see cref="GitHubRangeDownloader"/>,
/// reassembling the target zip. Reuses zsyncnet's algorithm; transport is ours.
///
/// NOTE: not yet exercised end-to-end — depends on a real .zsync asset existing on a release
/// (publisher step K2). The wiring is in place and compiles; verify once a .zsync is published.
/// </summary>
public sealed class ZsyncUpdater : IUpdater
{
    private readonly HttpClient _http;

    public ZsyncUpdater(HttpClient http) => _http = http;

    public UpdateMode Mode => UpdateMode.IncrementalZsync;

    public async Task UpdateAsync(UpdatePlan plan, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (plan.ZsyncUrl is null)
            throw new InvalidOperationException("ZsyncUpdater requires a ZsyncUrl; use FullDownloadUpdater otherwise.");

        // 1. Fetch + parse the control file.
        var zsyncBytes = await _http.GetByteArrayAsync(plan.ZsyncUrl, cancellationToken);
        var controlFile = new ControlFile(new MemoryStream(zsyncBytes));
        var targetLength = (long)controlFile.GetHeader().Length;

        // 2. Open the seed (previous zip) if we have one; first install has none.
        var seeds = new List<Stream>();
        if (!string.IsNullOrEmpty(plan.SeedZipPath) && File.Exists(plan.SeedZipPath))
            seeds.Add(new FileStream(plan.SeedZipPath, FileMode.Open, FileAccess.Read, FileShare.Read));

        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputZipPath)!);

        try
        {
            await using var working = new FileStream(plan.OutputZipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var downloader = new GitHubRangeDownloader(_http, plan.AssetUrl);

            // zsyncnet reports cumulative bytes; map onto our fraction against the target length.
            var inner = progress is null
                ? null
                : new Progress<ulong>(b => progress.Report(new UpdateProgress((long)b, targetLength)));

            // zsyncnet's Sync is synchronous; run it off the calling thread.
            await Task.Run(() => Zsync.Sync(controlFile, seeds, downloader, working, inner, cancellationToken), cancellationToken);
        }
        finally
        {
            foreach (var s in seeds)
                await s.DisposeAsync();
        }
    }
}
