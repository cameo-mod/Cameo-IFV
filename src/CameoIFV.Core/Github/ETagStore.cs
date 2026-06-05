using System.Text.Json;

namespace CameoIFV.Core.Github;

/// <summary>
/// Persists the ETag + cached JSON body per API URL so listings can use If-None-Match and take the
/// 304 (zero-byte) path. This is what spares us the old launcher's rate-limit waste: unchanged
/// release lists cost no quota and transfer nothing.
/// </summary>
public sealed class ETagStore
{
    private sealed class Entry
    {
        public string ETag { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;
    private readonly object _gate = new();

    public ETagStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public string? GetETag(string url)
    {
        lock (_gate)
            return _entries.TryGetValue(url, out var e) ? e.ETag : null;
    }

    public string? GetBody(string url)
    {
        lock (_gate)
            return _entries.TryGetValue(url, out var e) ? e.Body : null;
    }

    public void Save(string url, string etag, string body)
    {
        lock (_gate)
        {
            _entries[url] = new Entry { ETag = etag, Body = body };
            Persist();
        }
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new();
        }
        catch
        {
            // Corrupt cache is not fatal — fall back to an empty store and re-fetch.
        }

        return new();
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch
        {
            // Best-effort cache; ignore write failures.
        }
    }
}
