using System.Text.Json;
using System.Text.Json.Serialization;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Configuration;

public sealed class CatalogSeedCatalog
{
    private static readonly string[] StandaloneCampaignDlcTitles =
    [
        "Shadow of the Erdtree",
        "Phantom Liberty",
        "Blood and Wine",
        "Hearts of Stone",
        "Dawnguard",
        "Dragonborn",
        "Far Harbor",
        "Nuka-World",
        "Iceborne",
        "The Delicious Last Course"
    ];

    [JsonPropertyName("knownDlcTitles")]
    public IReadOnlyList<string> KnownDlcTitles { get; init; } = Array.Empty<string>();

    [JsonPropertyName("games")]
    public IReadOnlyList<CatalogSeedGame> Games { get; init; } = Array.Empty<CatalogSeedGame>();

    public CatalogSeedGame? FindMetadata(string title) =>
        Games.FirstOrDefault(game =>
            string.Equals(game.CanonicalTitle, title, StringComparison.OrdinalIgnoreCase));

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

    public EstimatedPlaytime? EstimatePlaytime(
        string title,
        string type,
        IReadOnlyList<string> genres)
    {
        var hasStandaloneCampaign = ContainsAny(
            title,
            StandaloneCampaignDlcTitles);
        if (!hasStandaloneCampaign &&
            (type.Equals("DLC", StringComparison.OrdinalIgnoreCase) ||
             GameTypeClassifier.Classify(title, KnownDlcTitles) == GameType.Dlc))
        {
            return null;
        }

        if (ContainsAny(title, "The Delicious Last Course"))
        {
            return new EstimatedPlaytime(180, 300, 480);
        }

        if (ContainsAny(title, "Hearts of Stone", "Dawnguard", "Dragonborn"))
        {
            return new EstimatedPlaytime(600, 900, 1_500);
        }

        if (ContainsAny(title, "Far Harbor", "Nuka-World"))
        {
            return new EstimatedPlaytime(480, 720, 1_200);
        }

        if (ContainsAny(title, "Phantom Liberty", "Blood and Wine"))
        {
            return new EstimatedPlaytime(900, 1_500, 2_400);
        }

        if (ContainsAny(title, "Shadow of the Erdtree"))
        {
            return new EstimatedPlaytime(1_500, 2_400, 3_600);
        }

        if (ContainsAny(title, "Iceborne"))
        {
            return new EstimatedPlaytime(1_800, 3_600, 6_000);
        }

        var classification = $"{title} {string.Join(' ', genres)}".ToLowerInvariant();
        if (ContainsAny(classification, "rpg", "role-playing", "role playing", "jrpg",
                "souls", "witcher", "baldur", "persona", "final fantasy", "fallout"))
        {
            return new EstimatedPlaytime(1_800, 3_300, 5_700);
        }

        if (ContainsAny(classification, "strategy", "simulation", "simulator", "tycoon",
                "city builder", "4x", "grand strategy", "management"))
        {
            return new EstimatedPlaytime(1_200, 2_400, 4_800);
        }

        if (ContainsAny(classification, "shooter", "fps", "first-person", "call of duty",
                "battlefield", "doom", "halo"))
        {
            return new EstimatedPlaytime(600, 900, 1_500);
        }

        if (ContainsAny(classification, "platformer", "indie", "metroidvania", "cuphead", "mario",
                "sonic", "celeste", "hollow knight"))
        {
            return new EstimatedPlaytime(360, 600, 1_080);
        }

        if (ContainsAny(classification, "adventure", "action", "narrative", "story rich",
                "visual novel"))
        {
            return new EstimatedPlaytime(720, 1_200, 2_100);
        }

        return new EstimatedPlaytime(900, 1_500, 2_700);
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));
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

    [JsonPropertyName("shortDescription")]
    public string? ShortDescription { get; init; }

    [JsonPropertyName("genres")]
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

    [JsonPropertyName("developer")]
    public string? Developer { get; init; }

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; init; }
}
