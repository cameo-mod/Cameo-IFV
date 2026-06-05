using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CameoIFV.Core.Model;

namespace CameoIFV.Core.Github;

public interface IReleaseProvider
{
    /// <summary>
    /// Lists releases for one source, resolving the installable asset (+ optional .zsync) for the
    /// given platform key (e.g. "windows"). Releases without a matching asset are omitted.
    /// </summary>
    Task<IReadOnlyList<ResolvedRelease>> ListAsync(ReleaseSource source, string platform, CancellationToken cancellationToken);
}

/// <summary>
/// Lists GitHub releases with ETag/If-None-Match conditional requests so unchanged listings take the
/// 304 path (no quota spend, no transfer) — fixing the old launcher's duplicate-call rate-limit waste.
/// </summary>
public sealed class GitHubReleaseProvider : IReleaseProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ETagStore _etags;
    private readonly int _perPage;

    public GitHubReleaseProvider(HttpClient http, ETagStore etags, int perPage = 30)
    {
        _http = http;
        _etags = etags;
        _perPage = perPage;
    }

    public async Task<IReadOnlyList<ResolvedRelease>> ListAsync(ReleaseSource source, string platform, CancellationToken cancellationToken)
    {
        // Source must describe the active platform, or there's nothing installable here.
        if (!source.Assets.TryGetValue(platform, out var filter) || string.IsNullOrEmpty(filter.AssetSuffix))
            return Array.Empty<ResolvedRelease>();

        var url = $"https://api.github.com/repos/{source.Repository}/releases?per_page={_perPage}";
        var body = await FetchWithETagAsync(url, cancellationToken);
        if (string.IsNullOrEmpty(body))
            return Array.Empty<ResolvedRelease>();

        var dtos = JsonSerializer.Deserialize<List<GitHubReleaseDto>>(body, JsonOptions) ?? new();

        var resolved = new List<ResolvedRelease>();
        foreach (var dto in dtos)
        {
            if (dto.Draft)
                continue;

            var asset = dto.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(filter.AssetSuffix, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                continue;

            Uri? zsyncUrl = null;
            if (!string.IsNullOrEmpty(filter.ZsyncSuffix))
            {
                var zsync = dto.Assets.FirstOrDefault(a =>
                    a.Name.EndsWith(filter.ZsyncSuffix, StringComparison.OrdinalIgnoreCase));
                if (zsync is not null)
                    zsyncUrl = new Uri(zsync.BrowserDownloadUrl);
            }

            resolved.Add(new ResolvedRelease
            {
                Channel = source.Channel,
                TagName = dto.TagName,
                DisplayName = string.IsNullOrWhiteSpace(dto.Name) ? dto.TagName : dto.Name,
                PublishedAt = dto.PublishedAt,
                Prerelease = dto.Prerelease,
                AssetUrl = new Uri(asset.BrowserDownloadUrl),
                AssetName = asset.Name,
                AssetSize = asset.Size,
                AssetId = asset.Id,
                AssetUpdatedAt = asset.UpdatedAt,
                ZsyncUrl = zsyncUrl,
            });
        }

        return resolved
            .OrderByDescending(r => r.PublishedAt)
            .ToList();
    }

    /// <summary>GET with If-None-Match; returns the (possibly cached) JSON body, or null on failure.</summary>
    private async Task<string?> FetchWithETagAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var cachedEtag = _etags.GetETag(url);
        if (cachedEtag is not null)
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cachedEtag));

        using var response = await _http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotModified)
            return _etags.GetBody(url); // 304: nothing transferred, reuse cached body.

        if (!response.IsSuccessStatusCode)
            return _etags.GetBody(url); // rate-limited / transient: degrade to last-known list if we have one.

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var etag = response.Headers.ETag?.Tag;
        if (!string.IsNullOrEmpty(etag))
            _etags.Save(url, etag, body);

        return body;
    }
}
