namespace CameoIFV.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"ifv-test-{Guid.NewGuid():N}");

    public TempDir()
    {
        Directory.CreateDirectory(Path);
    }

    public string Write(string relativePath, string contents)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best effort cleanup for temp test data.
        }
    }
}
