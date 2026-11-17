using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RadarOfertasMultinucleo.Domain;
using RadarOfertasMultinucleo.Infrastructure.Database;

namespace RadarOfertasMultinucleo.Pipeline;

public sealed class PriceHistorySeedService
{
    private static readonly DateOnly StartDate = new(2026, 11, 9);
    private static readonly TimeSpan CostaRicaOffset = TimeSpan.FromHours(-6);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SqliteCatalogRepository _repository;

    public PriceHistorySeedService(SqliteCatalogRepository repository)
    {
        _repository = repository;
    }

    public async Task<PriceHistorySeedResult> SeedThroughTodayAsync(
        CancellationToken cancellationToken)
    {
        var endDate = DateOnly.FromDateTime(
            DateTimeOffset.UtcNow.ToOffset(CostaRicaOffset).DateTime);
        if (endDate < StartDate)
        {
            throw new InvalidOperationException(
                $"La fecha actual {endDate:yyyy-MM-dd} es anterior al inicio del historial.");
        }

        var publishedCatalog = await ReadJsonAsync<CatalogSeedGame>(
            Path.GetFullPath(Path.Combine("web", "data", "catalog.json")),
            cancellationToken);
        var publishedHistory = await ReadJsonAsync<PriceHistoryEntry>(
            Path.GetFullPath(Path.Combine("web", "data", "history.json")),
            cancellationToken);
        var databaseHistory = await _repository.ReadPriceHistoryAsync(cancellationToken);
        var databaseMetadata = await _repository.ReadCatalogMetadataAsync(cancellationToken);

        var titles = databaseMetadata.ToDictionary(
            game => NormalizeGameId(game.GameId),
            game => game.CanonicalTitle,
            StringComparer.OrdinalIgnoreCase);
        var pricingReferences = databaseMetadata.ToDictionary(
            game => NormalizeGameId(game.GameId),
            game => (game.Msrp, game.Currency),
            StringComparer.OrdinalIgnoreCase);
        var titlesByProductUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in publishedCatalog)
        {
            if (!Guid.TryParse(game.GameId, out var gameId) ||
                string.IsNullOrWhiteSpace(game.CanonicalTitle))
            {
                continue;
            }

            var parentId = gameId.ToString("D");
            titles.TryAdd(parentId, game.CanonicalTitle);
            pricingReferences[parentId] = MergePricingReference(
                pricingReferences.GetValueOrDefault(parentId),
                game.Msrp,
                game.Currency);
            foreach (var offer in game.Offers ?? Array.Empty<CatalogSeedOffer>())
            {
                var productKey = NormalizeProductUrl(offer.ProductUrl);
                if (!string.IsNullOrEmpty(productKey))
                {
                    titlesByProductUrl.TryAdd(productKey, game.CanonicalTitle);
                }

                if (!Guid.TryParse(offer.GameId, out var offerGameId)) continue;
                var offerId = offerGameId.ToString("D");
                titles.TryAdd(offerId, game.CanonicalTitle);
                pricingReferences[offerId] = MergePricingReference(
                    pricingReferences.GetValueOrDefault(offerId),
                    game.Msrp,
                    game.Currency);
            }
        }
        foreach (var entry in publishedHistory)
        {
            var productKey = NormalizeProductUrl(entry.ProductUrl);
            if (Guid.TryParse(entry.GameId, out var gameId) &&
                !string.IsNullOrEmpty(productKey) &&
                titlesByProductUrl.TryGetValue(productKey, out var title))
            {
                titles.TryAdd(gameId.ToString("D"), title);
            }
        }

        var catalogObservations = publishedCatalog
            .SelectMany(game => (game.Offers ?? Array.Empty<CatalogSeedOffer>())
                .Where(offer => offer.CurrentPrice is not null)
                .Select(offer => new PriceHistoryEntry(
                    offer.ObservationId,
                    NormalizeGameId(offer.GameId),
                    offer.SourceId,
                    offer.CurrentPrice,
                    offer.Currency,
                    offer.Format,
                    offer.CapturedAt,
                    offer.ProductUrl,
                    offer.RegularPrice,
                    offer.IsDeal,
                    offer.DiscountPercent,
                    offer.IsEstimated)));
        var previousObservations = databaseHistory
            .Concat(publishedHistory)
            .Concat(catalogObservations)
            .Where(entry => entry.CurrentPrice is not null &&
                entry.CapturedAt != default &&
                Guid.TryParse(entry.GameId, out _) &&
                !string.IsNullOrWhiteSpace(entry.SourceId))
            .Select(entry => entry with
            {
                GameId = NormalizeGameId(entry.GameId),
                SourceId = entry.SourceId.Trim().ToLowerInvariant()
            })
            .GroupBy(ObservationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => !entry.IsEstimated)
                .ThenByDescending(entry => entry.CapturedAt)
                .First())
            .ToArray();

        var knownObservations = databaseHistory
            .Select(ObservationKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var importedObservations = 0;
        foreach (var entry in previousObservations.OrderBy(entry => entry.CapturedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (knownObservations.Contains(ObservationKey(entry)) ||
                !titles.TryGetValue(entry.GameId, out var title))
            {
                continue;
            }

            await _repository.SavePriceObservationAsync(
                Guid.Parse(entry.GameId),
                title,
                entry.SourceId,
                entry.ProductUrl,
                entry.CurrentPrice,
                entry.RegularPrice,
                entry.Currency,
                entry.Format,
                entry.IsDeal,
                entry.DiscountPercent,
                entry.CapturedAt,
                cancellationToken,
                entry.IsEstimated);
            knownObservations.Add(ObservationKey(entry));
            importedObservations++;
        }

        var allHistory = (await _repository.ReadPriceHistoryAsync(cancellationToken))
            .Concat(previousObservations)
            .Where(entry => entry.CurrentPrice is not null && entry.CapturedAt != default)
            .GroupBy(ObservationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => !entry.IsEstimated)
                .ThenByDescending(entry => entry.CapturedAt)
                .First())
            .ToArray();
        var observationsByPair = allHistory
            .Where(entry => entry.CurrentPrice is not null)
            .GroupBy(entry => $"{entry.GameId}|{entry.SourceId}", StringComparer.OrdinalIgnoreCase);
        var pairCount = 0;
        var estimatedInserted = 0;
        var pairsWithRealCurrentPrice = 0;

        foreach (var pair in observationsByPair)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = pair
                .Where(entry => !entry.IsEstimated && entry.CurrentPrice is not null)
                .OrderBy(entry => entry.CapturedAt)
                .ToArray();
            if (actual.Length == 0) continue;

            var latest = actual[^1];
            var lastRegular = actual
                .Where(entry => entry.RegularPrice is > 0)
                .OrderByDescending(entry => entry.CapturedAt)
                .Select(entry => entry.RegularPrice)
                .FirstOrDefault();
            var msrp = pricingReferences.TryGetValue(latest.GameId, out var pricingReference) &&
                string.Equals(pricingReference.Currency, latest.Currency, StringComparison.OrdinalIgnoreCase)
                ? pricingReference.Msrp
                : null;
            var regularPrice = Math.Max(
                latest.CurrentPrice!.Value,
                lastRegular ?? msrp ?? 0m);
            if (regularPrice <= 0) regularPrice = latest.CurrentPrice.Value;
            var currentPrice = latest.CurrentPrice.Value;
            var existingDates = pair
                .Select(entry => CostaRicaDate(entry.CapturedAt))
                .ToHashSet();
            var hasRealCurrentPrice = actual.Any(entry =>
                CostaRicaDate(entry.CapturedAt) == endDate);
            if (hasRealCurrentPrice) pairsWithRealCurrentPrice++;
            if (!titles.TryGetValue(latest.GameId, out var title)) continue;
            pairCount++;

            var endHistoryDate = endDate.AddDays(-1);
            var interpolationDays = Math.Max(1, endHistoryDate.DayNumber - StartDate.DayNumber);
            for (var date = StartDate; date <= endDate; date = date.AddDays(1))
            {
                if (existingDates.Contains(date)) continue;

                var isEstimated = true;
                decimal price;
                if (date == endDate)
                {
                    if (hasRealCurrentPrice) continue;
                    price = latest.CurrentPrice.Value;
                }
                else
                {
                    var progress = Math.Clamp(
                        (decimal)(date.DayNumber - StartDate.DayNumber) / interpolationDays,
                        0m,
                        1m);
                    price = Math.Round(
                        regularPrice - ((regularPrice - currentPrice) * progress),
                        2,
                        MidpointRounding.AwayFromZero);
                }

                var discountPercent = CalculateDiscount(price, regularPrice);
                await _repository.SavePriceObservationAsync(
                    Guid.Parse(latest.GameId),
                    title,
                    latest.SourceId,
                    latest.ProductUrl,
                    price,
                    regularPrice,
                    latest.Currency,
                    latest.Format,
                    discountPercent is not null,
                    discountPercent,
                    CostaRicaNoon(date),
                    cancellationToken,
                    isEstimated);
                existingDates.Add(date);
                estimatedInserted++;
            }
        }

        return new PriceHistorySeedResult(
            importedObservations,
            estimatedInserted,
            pairCount,
            pairsWithRealCurrentPrice,
            endDate);
    }

    private static DateOnly CostaRicaDate(DateTimeOffset capturedAt) =>
        DateOnly.FromDateTime(capturedAt.ToOffset(CostaRicaOffset).DateTime);

    private static DateTimeOffset CostaRicaNoon(DateOnly date) =>
        new DateTimeOffset(
            date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified),
            CostaRicaOffset)
            .ToUniversalTime();

    private static string NormalizeGameId(string gameId) =>
        Guid.TryParse(gameId, out var parsed) ? parsed.ToString("D") : gameId;

    private static string NormalizeProductUrl(string? productUrl)
    {
        if (!Uri.TryCreate(productUrl, UriKind.Absolute, out var uri)) return string.Empty;
        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private static (decimal? Msrp, string? Currency) MergePricingReference(
        (decimal? Msrp, string? Currency) existing,
        decimal? msrp,
        string? currency) =>
        (existing.Msrp ?? msrp, existing.Currency ?? currency);

    private static string ObservationKey(PriceHistoryEntry entry) =>
        string.Join(
            "|",
            entry.GameId,
            entry.SourceId,
            entry.CapturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            entry.CurrentPrice?.ToString(CultureInfo.InvariantCulture),
            entry.IsEstimated);

    private static decimal? CalculateDiscount(decimal currentPrice, decimal regularPrice)
    {
        if (regularPrice <= 0 || currentPrice >= regularPrice) return null;
        var discount = Math.Round(
            100m * (regularPrice - currentPrice) / regularPrice,
            2,
            MidpointRounding.AwayFromZero);
        return discount > 0 ? discount : null;
    }

    private static async Task<T[]> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Array.Empty<T>();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T[]>(stream, JsonOptions, cancellationToken)
            ?? Array.Empty<T>();
    }

    private sealed record CatalogSeedGame(
        string GameId,
        string CanonicalTitle,
        decimal? Msrp,
        string? Currency,
        IReadOnlyList<CatalogSeedOffer>? Offers);

    private sealed record CatalogSeedOffer(
        long ObservationId,
        string GameId,
        string SourceId,
        decimal? CurrentPrice,
        decimal? RegularPrice,
        string? Currency,
        OfferFormat Format,
        bool IsDeal,
        decimal? DiscountPercent,
        DateTimeOffset CapturedAt,
        string? ProductUrl,
        bool IsEstimated = false);
}

public sealed record PriceHistorySeedResult(
    int ImportedObservations,
    int EstimatedObservations,
    int GameStorePairs,
    int PairsWithRealCurrentPrice,
    DateOnly ThroughDate);
