using System.Reflection;
using CameoIFV.Core.Update;
using Xunit;
using zsyncnet;

namespace CameoIFV.Core.Tests;

/// <summary>
/// The cache downloader backs zsync's assembly pass: it must return exactly the requested
/// byte range from the prefetched cache file. (Internal type, reached via reflection.)
/// </summary>
public class CacheRangeDownloaderTests
{
    [Fact]
    public void DownloadRange_ReturnsExactRequestedBytes()
    {
        using var dir = new TempDir();
        var cachePath = System.IO.Path.Combine(dir.Path, "delta.cache");
        var content = new byte[64 * 1024];
        for (var i = 0; i < content.Length; i++) content[i] = (byte)(i * 7 % 256);
        File.WriteAllBytes(cachePath, content);

        var downloader = CreateDownloader(cachePath);
        try
        {
            // A mid-file range must come back byte-exact and exactly the requested length.
            using var stream = downloader.DownloadRange(10_000, 10_000 + 5_000);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var got = ms.ToArray();

            Assert.Equal(5_000, got.Length);
            Assert.Equal(content.Skip(10_000).Take(5_000), got);
        }
        finally
        {
            ((IDisposable)downloader).Dispose();
        }
    }

    // CacheRangeDownloader is internal; construct it via reflection so the test stays in the public test asm.
    private static IRangeDownloader CreateDownloader(string cachePath)
    {
        var type = typeof(ZsyncUpdater).Assembly.GetType("CameoIFV.Core.Update.CacheRangeDownloader")!;
        return (IRangeDownloader)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new object[] { cachePath }, culture: null)!;
    }
}
