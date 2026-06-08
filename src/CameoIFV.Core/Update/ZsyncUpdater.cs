using System.Diagnostics;
using zsyncnet;

namespace CameoIFV.Core.Update;

/// <summary>
/// Incremental path, in three phases so the network part can be parallelised (zsyncnet's own Sync
/// fetches ranges strictly sequentially, which is latency-bound — ~30 ms/round-trip, thousands of
/// ranges — and ends up slower than a full download):
///
///   1. DISCOVER — run zsyncnet's matcher against the seed with a recording, zero-returning
///      downloader (no network) to learn exactly which byte ranges must be fetched.
///   2. PREFETCH — download those ranges in parallel into a sparse cache file (no over-fetch).
///   3. ASSEMBLE — run zsyncnet against the local cache (instant) to reconstruct + verify SHA-1.
///
/// Verified end-to-end against a published .zsync sidecar (Cameo playtest): ~85% reused from the
/// seed, ~15% fetched. zsyncnet checks the whole-file SHA-1 from the control file and throws on
/// mismatch, so a bad assembly never reaches the seed slot. Phase milestones are reported via
/// <see cref="UpdateProgress.Message"/> so they surface in the launcher's session log.
/// </summary>
public sealed class ZsyncUpdater : IUpdater
{
    private const int MaxConcurrency = 16;
    private const int MaxRangeAttempts = 4;

    private readonly HttpClient _http;

    public ZsyncUpdater(HttpClient http) => _http = http;

    public UpdateMode Mode => UpdateMode.IncrementalZsync;

    public async Task UpdateAsync(UpdatePlan plan, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (plan.ZsyncUrl is null)
            throw new InvalidOperationException("ZsyncUpdater requires a ZsyncUrl; use FullDownloadUpdater otherwise.");

        void Log(long bytes, long total, string message) => progress?.Report(new UpdateProgress(bytes, total, message));

        // 1. Fetch + parse the control file.
        Log(0, 0, $"zsync: fetching control file ({plan.ZsyncUrl.Segments[^1]})");
        var zsyncBytes = await _http.GetByteArrayAsync(plan.ZsyncUrl, cancellationToken);
        ControlFile NewControl() => new(new MemoryStream(zsyncBytes));
        var header = NewControl().GetHeader();
        var targetLength = (long)header.Length;
        var seedSize = !string.IsNullOrEmpty(plan.SeedZipPath) && File.Exists(plan.SeedZipPath)
            ? new FileInfo(plan.SeedZipPath).Length
            : 0;
        Log(0, 0, $"zsync: control {Bytes(zsyncBytes.Length)}, target {Bytes(targetLength)}, "
            + $"blocksize {header.BlockSize}, seed {(seedSize > 0 ? Bytes(seedSize) : "(none)")}");

        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputZipPath)!);

        // 2. DISCOVER the ranges that don't match the seed (reads the seed, no network).
        Log(0, 0, "zsync: scanning seed for reusable blocks...");
        var swScan = Stopwatch.StartNew();
        var ranges = await Task.Run(() => DiscoverRanges(NewControl(), plan.SeedZipPath, cancellationToken), cancellationToken);
        swScan.Stop();

        long deltaTotal = 0;
        foreach (var r in ranges) deltaTotal += r.To - r.From;
        var reused = targetLength - deltaTotal;
        Log(0, deltaTotal, $"zsync: {Percent(reused, targetLength)} reused from seed ({Bytes(reused)}); "
            + $"fetching {Bytes(deltaTotal)} in {ranges.Count:N0} ranges (scan {swScan.Elapsed.TotalSeconds:F1}s)");

        var cachePath = plan.OutputZipPath + ".cache";
        try
        {
            // 3. PREFETCH the ranges in parallel into a sparse cache file.
            if (ranges.Count > 0)
            {
                Log(0, deltaTotal, $"zsync: downloading delta with {MaxConcurrency} parallel connections...");
                var swDl = Stopwatch.StartNew();
                await PrefetchRangesAsync(plan.AssetUrl, ranges, cachePath, targetLength, deltaTotal, progress, cancellationToken);
                swDl.Stop();
                var mbps = deltaTotal / 1024.0 / 1024 / Math.Max(0.001, swDl.Elapsed.TotalSeconds);
                Log(deltaTotal, deltaTotal, $"zsync: delta fetched in {swDl.Elapsed.TotalSeconds:F1}s ({mbps:F1} MB/s)");
            }
            else
            {
                using var f = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
                f.SetLength(targetLength);
                Log(0, 0, "zsync: target is identical to the seed; nothing to download");
            }

            // 4. ASSEMBLE from seed + cache and verify the whole-file SHA-1 (no network).
            Log(deltaTotal, deltaTotal, "zsync: assembling + verifying SHA-1...");
            var swAsm = Stopwatch.StartNew();
            await Task.Run(() => Assemble(NewControl(), plan.SeedZipPath, cachePath, plan.OutputZipPath, cancellationToken), cancellationToken);
            swAsm.Stop();
            Log(deltaTotal, deltaTotal, $"zsync: assembled + verified OK ({swAsm.Elapsed.TotalSeconds:F1}s)");
        }
        finally
        {
            TryDelete(cachePath);
        }
    }

    /// <summary>
    /// Phase 1: run zsyncnet's matcher with a downloader that records each requested range and
    /// returns zeros. The final SHA-1 verify fails on the zero-filled sink (expected) — by then
    /// every range has been recorded.
    /// </summary>
    private static List<DownloadRange> DiscoverRanges(ControlFile controlFile, string? seedPath, CancellationToken cancellationToken)
    {
        var seeds = OpenSeeds(seedPath);
        try
        {
            var recorder = new RecordingDownloader();
            using var sink = new DiscardStream();
            try
            {
                Zsync.Sync(controlFile, seeds, recorder, sink, null, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Verification fails because the sink holds zeros — ranges are already recorded.
            }

            return recorder.Ranges;
        }
        finally
        {
            foreach (var s in seeds) s.Dispose();
        }
    }

    /// <summary>Phase 2: download the discovered ranges concurrently into a sparse cache file.</summary>
    private async Task PrefetchRangesAsync(
        Uri assetUrl, List<DownloadRange> ranges, string cachePath, long targetLength, long deltaTotal,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        // Pre-size the cache to the full target so positioned writes land at absolute offsets.
        using (var f = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None))
            f.SetLength(targetLength);

        using var cache = File.OpenHandle(cachePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var downloader = new GitHubRangeDownloader(_http, assetUrl);

        // Resolve GitHub's signed CDN URL once so the parallel tasks don't each pay a 302 redirect.
        using (var warm = await downloader.DownloadRangeAsync(ranges[0].From, ranges[0].From + 1, cancellationToken))
            await warm.CopyToAsync(Stream.Null, cancellationToken);

        long done = 0;
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var tasks = new List<Task>(ranges.Count);
        foreach (var range in ranges)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var bytes = await FetchWithRetryAsync(downloader, range, cancellationToken).ConfigureAwait(false);
                    await RandomAccess.WriteAsync(cache, bytes, range.From, cancellationToken).ConfigureAwait(false);
                    var total = Interlocked.Add(ref done, bytes.Length);
                    progress?.Report(new UpdateProgress(total, deltaTotal));
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<byte[]> FetchWithRetryAsync(GitHubRangeDownloader downloader, DownloadRange range, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = await downloader.DownloadRangeAsync(range.From, range.To, cancellationToken).ConfigureAwait(false);
                using var buffer = new MemoryStream((int)(range.To - range.From));
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                return buffer.ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (attempt < MaxRangeAttempts)
            {
                await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Phase 3: reconstruct the target from seed + cache; zsyncnet verifies the SHA-1.</summary>
    private static void Assemble(ControlFile controlFile, string? seedPath, string cachePath, string outputPath, CancellationToken cancellationToken)
    {
        var seeds = OpenSeeds(seedPath);
        try
        {
            using var working = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var downloader = new CacheRangeDownloader(cachePath);
            Zsync.Sync(controlFile, seeds, downloader, working, null, cancellationToken);
        }
        finally
        {
            foreach (var s in seeds) s.Dispose();
        }
    }

    private static List<Stream> OpenSeeds(string? seedPath)
    {
        var seeds = new List<Stream>();
        if (!string.IsNullOrEmpty(seedPath) && File.Exists(seedPath))
            seeds.Add(new FileStream(seedPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        return seeds;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort: the cache is scratch */ }
    }

    private static string Bytes(long n)
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

    private static string Percent(long part, long whole) => whole > 0 ? $"{100.0 * part / whole:F1}%" : "0%";

    /// <summary>A requested byte range [From, To) — To exclusive, matching zsyncnet.</summary>
    private readonly record struct DownloadRange(long From, long To);

    /// <summary>Phase-1 downloader: records each requested range and returns zeros (no network).</summary>
    private sealed class RecordingDownloader : IRangeDownloader
    {
        public List<DownloadRange> Ranges { get; } = new();

        public Stream DownloadRange(long from, long to)
        {
            Ranges.Add(new DownloadRange(from, to));
            return new ZeroStream(to - from);
        }

        public Stream Download() => new ZeroStream(0);
    }

    /// <summary>Read-only stream of N zero bytes — lets zsyncnet "receive" a range without any network.</summary>
    private sealed class ZeroStream : Stream
    {
        private readonly long _length;
        private long _pos;
        public ZeroStream(long length) => _length = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _pos;
            if (remaining <= 0) return 0;
            var n = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Seekable sink that discards writes but tracks length — output for the phase-1 scan.</summary>
    private sealed class DiscardStream : Stream
    {
        private long _pos;
        private long _len;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _len - _pos;
            if (remaining <= 0) return 0;
            var n = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => _len + offset,
                _ => _pos,
            };
            return _pos;
        }
        public override void SetLength(long value) => _len = value;
        public override void Write(byte[] buffer, int offset, int count)
        {
            _pos += count;
            if (_pos > _len) _len = _pos;
        }
    }
}
