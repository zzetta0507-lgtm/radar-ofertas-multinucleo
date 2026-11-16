using System.Text.Json;
using System.Text.Json.Serialization;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Configuration;

public sealed class CatalogSeedCatalog
{
    [JsonPropertyName("knownDlcTitles")]
    public IReadOnlyList<string> KnownDlcTitles { get; init; } = Array.Empty<string>();

    [JsonPropertyName("games")]
    public IReadOnlyList<CatalogSeedGame> Games { get; init; } = Array.Empty<CatalogSeedGame>();

    public static async Task<CatalogSeedCatalog> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = File.OpenRead(fullPath);
        var catalog = await JsonSerializer.DeserializeAsync<CatalogSeedCatalog>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken) ?? throw new InvalidDataException(
                $"El catálogo de semillas está vacío o no es válido: {fullPath}.");

        catalog.Validate(fullPath);
        return catalog;
    }

    public void Validate(string path)
    {
        if (KnownDlcTitles.Count == 0 || Games.Count < 10)
        {
            throw new InvalidDataException(
                $"El catálogo de semillas '{path}' debe declarar palabras conocidas y al menos 10 juegos DLC.");
        }

        if (KnownDlcTitles.Any(string.IsNullOrWhiteSpace) ||
            Games.Any(game =>
                string.IsNullOrWhiteSpace(game.ExternalId) ||
                string.IsNullOrWhiteSpace(game.CanonicalTitle) ||
                game.Platforms.Count == 0 ||
                !Uri.TryCreate(game.SourceUrl, UriKind.Absolute, out var sourceUri) ||
                sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException(
                $"El catálogo de semillas '{path}' contiene títulos, IDs, plataformas o URLs inválidas.");
        }

        if (Games.Select(game => game.ExternalId).Distinct(StringComparer.Ordinal).Count() != Games.Count)
        {
            throw new InvalidDataException(
                $"El catálogo de semillas '{path}' contiene IDs externos duplicados.");
        }

        if (Games.Any(game =>
                GameTypeClassifier.Classify(game.CanonicalTitle, KnownDlcTitles) != GameType.Dlc))
        {
            throw new InvalidDataException(
                $"Todos los juegos del catálogo de semillas '{path}' deben clasificarse como DLC.");
        }
    }
}

public sealed class CatalogSeedGame
{
    [JsonPropertyName("externalId")]
    public string ExternalId { get; init; } = string.Empty;

    [JsonPropertyName("canonicalTitle")]
    public string CanonicalTitle { get; init; } = string.Empty;

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; init; } = string.Empty;

    [JsonPropertyName("coverImageUrl")]
    public string? CoverImageUrl { get; init; }
}
