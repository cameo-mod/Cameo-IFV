using System.Text.Json;
using System.Text.Json.Serialization;
using CameoIFV.Core.Model;

namespace CameoIFV.Core.Config;

/// <summary>
/// Loads a <see cref="ModCatalog"/> from JSON. Centralises the serializer options so the
/// camelCase config files bind to the PascalCase model and string enums work everywhere.
/// </summary>
public static class CatalogLoader
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ModCatalog Parse(string json)
        => JsonSerializer.Deserialize<ModCatalog>(json, Options)
           ?? throw new JsonException("Catalog JSON deserialized to null.");

    public static ModCatalog Load(string path)
        => Parse(File.ReadAllText(path));
}
