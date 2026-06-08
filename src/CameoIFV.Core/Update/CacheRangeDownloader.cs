using Microsoft.Win32.SafeHandles;
using zsyncnet;

namespace CameoIFV.Core.Update;

/// <summary>
/// Serves zsyncnet's range requests from a local cache file that the parallel prefetcher already
/// filled (each changed block written at its absolute offset). No network — this backs the assembly
/// pass, after the latency-bound downloading is done, so zsyncnet reconstructs + verifies at disk speed.
/// </summary>
internal sealed class CacheRangeDownloader : IRangeDownloader, IDisposable
{
    private readonly SafeFileHandle _handle;

    public CacheRangeDownloader(string cachePath)
        => _handle = File.OpenHandle(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public Stream DownloadRange(long from, long to)
    {
        var length = (int)(to - from);
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = RandomAccess.Read(_handle, buffer.AsSpan(read), from + read);
            if (n == 0) break; // hit EOF — a missing block; the SHA-1 verify will catch it.
            read += n;
        }

        return new MemoryStream(buffer, 0, length, writable: false);
    }

    public Stream Download() => throw new NotSupportedException("Cache downloader serves ranges only.");

    public void Dispose() => _handle.Dispose();
}
