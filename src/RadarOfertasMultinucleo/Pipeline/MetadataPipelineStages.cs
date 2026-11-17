using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;
using RadarOfertasMultinucleo.Infrastructure.Database;
using RadarOfertasMultinucleo.Infrastructure.Replay;

namespace RadarOfertasMultinucleo.Pipeline;

public sealed class StoreAndMetadataDownloadStage : IDownloadStage
{
    private const string SteamSearchUrl = "https://store.steampowered.com/api/storesearch/";
    private const string SteamAppDetailsUrl = "https://store.steampowered.com/api/appdetails";
    private const string GamersGateSearchUrl = "https://www.gamersgate.com/api/games/";
    private const string EpicCatalogUrl =
        "https://store-site-backend-static.ak.epicgames.com/api/es-ES/content/products";
    private const string GogCatalogUrl = "https://catalog.gog.com/v1/catalog";
    private const string CheapSharkDealsUrl = "https://www.cheapshark.com/api/1.0/deals";
    private const string CheapSharkStoresUrl = "https://www.cheapshark.com/api/1.0/stores";
    private const string EnebaSearchUrl = "https://www.eneba.com/store/all?text=";
    private readonly ReplayableHttpClient _http;
    private readonly double _matchingThreshold;
    private readonly PipelineRunMetrics? _metrics;
    private readonly HashSet<string> _storeSources;
    private readonly ConcurrentDictionary<string, byte> _reportedFailures = new(StringComparer.Ordinal);
    private readonly Lazy<Task<IReadOnlyDictionary<string, string>>> _cheapSharkStores;

    public StoreAndMetadataDownloadStage(
        ReplayableHttpClient http,
        double matchingThreshold,
        IReadOnlyCollection<string>? storeSources = null,
        PipelineRunMetrics? metrics = null)
    {
        _http = http;
        _matchingThreshold = matchingThreshold;
        _metrics = metrics;
        _storeSources = new HashSet<string>(
            storeSources ?? ["steam", "epicgames", "gog", "eneba", "humble", "gmg", "gamersgate"],
            StringComparer.OrdinalIgnoreCase);
        _cheapSharkStores = new Lazy<Task<IReadOnlyDictionary<string, string>>>(
            () => LoadCheapSharkStoresAsync(CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<IReadOnlyList<SourceResponse>> DownloadAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        var responses = new List<SourceResponse>();
        if (game.ExternalId.StartsWith("opencritic:", StringComparison.Ordinal))
        {
            var openCriticPage = await TryGetPageAsync(
                new Uri(game.SourceUrl),
                "opencritic",
                cancellationToken);
            if (openCriticPage is not null)
            {
                responses.Add(new SourceResponse(
                    game.Id,
                    "opencritic",
                    game.CanonicalTitle,
                    game.Platforms,
                    game.SourceUrl,
                    openCriticPage.ContentType,
                    openCriticPage.Body,
                    openCriticPage.RetrievedAt,
                    true,
                    null,
                    game.Msrp));
            }
        }

        var steamTask = _storeSources.Contains("steam")
            ? FindSteamListingAsync(game, cancellationToken)
            : Task.FromResult<StoreListingPayload?>(null);
        var epicTask = _storeSources.Contains("epicgames")
            ? FindEpicListingAsync(game, cancellationToken)
            : Task.FromResult<StoreListingPayload?>(null);
        var gogTask = _storeSources.Contains("gog")
            ? FindGogListingAsync(game, cancellationToken)
            : Task.FromResult<StoreListingPayload?>(null);
        var enebaTask = _storeSources.Contains("eneba")
            ? FindEnebaListingAsync(game, cancellationToken)
            : Task.FromResult<StoreListingPayload?>(null);
        var cheapSharkTask = _storeSources.Any(IsCheapSharkSource)
            ? FindCheapSharkListingsAsync(game, cancellationToken)
            : Task.FromResult<IReadOnlyList<SourceResponse>>(Array.Empty<SourceResponse>());
        var gamersGateTask = _storeSources.Contains("gamersgate")
            ? FindGamersGateListingAsync(game, cancellationToken)
            : Task.FromResult<StoreListingPayload?>(null);
        await Task.WhenAll(
            steamTask,
            epicTask,
            gogTask,
            enebaTask,
            cheapSharkTask,
            gamersGateTask);

        var steamListing = await steamTask;
        if (steamListing is not null)
        {
            var body = JsonSerializer.Serialize(steamListing);
            responses.Add(new SourceResponse(
                game.Id,
                "steam",
                game.CanonicalTitle,
                game.Platforms,
                steamListing.ProductUrl,
                "application/json",
                body,
                steamListing.RetrievedAt,
                true,
                null,
                game.Msrp));

            if (steamListing.Rating is not null)
            {
                responses.Add(new SourceResponse(
                    game.Id,
                    "metacritic",
                    game.CanonicalTitle,
                    game.Platforms,
                    null,
                    "application/json",
                    body,
                    steamListing.RetrievedAt,
                    true,
                    null,
                    game.Msrp));
            }
        }

        var epicListing = await epicTask;
        if (epicListing is not null)
        {
            responses.Add(CreateStoreResponse(game, "epicgames", epicListing));
        }

        var gogListing = await gogTask;
        if (gogListing is not null)
        {
            responses.Add(CreateStoreResponse(game, "gog", gogListing));
        }

        var enebaListing = await enebaTask;
        if (enebaListing is not null)
        {
            responses.Add(CreateStoreResponse(game, "eneba", enebaListing));
        }

        responses.AddRange(await cheapSharkTask);

        var gamersGateListing = await gamersGateTask;
        if (gamersGateListing is not null)
        {
            responses.Add(new SourceResponse(
                game.Id,
                "gamersgate",
                game.CanonicalTitle,
                game.Platforms,
                gamersGateListing.ProductUrl,
                "application/json",
                JsonSerializer.Serialize(gamersGateListing),
                gamersGateListing.RetrievedAt,
                true,
                null,
                game.Msrp));
        }

        return responses;
    }

    private static SourceResponse CreateStoreResponse(
        GameRecord game,
        string sourceId,
        StoreListingPayload listing) =>
        new(
            game.Id,
            sourceId,
            game.CanonicalTitle,
            game.Platforms,
            listing.ProductUrl,
            "application/json",
            JsonSerializer.Serialize(listing),
            listing.RetrievedAt,
            true,
            null,
            game.Msrp);

    private async Task<StoreListingPayload?> FindEpicListingAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{EpicCatalogUrl}?count=5&country=CR&keywords={Uri.EscapeDataString(game.CanonicalTitle)}&locale=es-ES");
        var page = await TryGetPageAsync(uri, "epicgames", cancellationToken);
        if (page is null) return null;

        try
        {
            using var document = JsonDocument.Parse(page.Body);
            var best = FindMatchingProduct(document.RootElement, game.CanonicalTitle)
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            if (best.Name is null || best.Score < _matchingThreshold)
            {
                return null;
            }

            var price = FindProperty(best.Item, "totalPrice");
            var original = GetNumber(price, "originalPrice");
            var current = GetNumber(price, "discountPrice");
            var currency = GetString(price, "currencyCode");
            if (original is not null) original /= 100m;
            if (current is not null) current /= 100m;
            current ??= original;
            var slug = GetString(best.Item, "urlSlug") ??
                GetString(best.Item, "productSlug") ??
                GetString(best.Item, "id");
            var url = slug is null
                ? null
                : $"https://store.epicgames.com/es-ES/p/" +
                    string.Join("/", slug.Split('/').Select(Uri.EscapeDataString));
            return current is null
                ? null
                : new StoreListingPayload(
                    best.Name,
                    GetString(best.Item, "id") ?? slug ?? best.Name,
                    current,
                    original,
                    currency,
                    null,
                    url,
                    null,
                    page.RetrievedAt,
                    GetNumber(price, "discountPercent") ?? GetNumber(price, "discount"));
        }
        catch (JsonException exception)
        {
            ReportFailure("epicgames", uri, exception.Message);
            return null;
        }
    }

    private async Task<StoreListingPayload?> FindGogListingAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{GogCatalogUrl}?countryCode=CR&currencyCode=USD&limit=5" +
            $"&query=like%3A{Uri.EscapeDataString(game.CanonicalTitle)}");
        var page = await TryGetPageAsync(uri, "gog", cancellationToken);
        if (page is null) return null;

        try
        {
            using var document = JsonDocument.Parse(page.Body);
            var best = FindMatchingProduct(document.RootElement, game.CanonicalTitle)
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            if (best.Name is null || best.Score < _matchingThreshold) return null;

            var price = FindProperty(best.Item, "price");
            var finalMoney = FindProperty(price, "finalMoney");
            var baseMoney = FindProperty(price, "baseMoney");
            var current = GetNumber(finalMoney, "amount") ??
                ParsePriceText(GetString(price, "final")) ??
                GetNumber(price, "finalAmount") ??
                GetNumber(price, "amount");
            var regular = GetNumber(baseMoney, "amount") ??
                ParsePriceText(GetString(price, "base")) ??
                GetNumber(price, "baseAmount") ??
                GetNumber(price, "basePrice");
            if (current is null) return null;
            var path = GetString(best.Item, "storeLink") ??
                GetString(best.Item, "productCardUrl") ??
                GetString(best.Item, "url");
            var productUrl = path is null
                ? null
                : new Uri(new Uri("https://www.gog.com"), path).AbsoluteUri;
            productUrl ??= GetString(best.Item, "slug") is { } slug
                ? $"https://www.gog.com/en/game/{Uri.EscapeDataString(slug)}"
                : null;
            return new StoreListingPayload(
                best.Name,
                GetString(best.Item, "id") ?? best.Name,
                current,
                regular,
                GetString(finalMoney, "currency") ?? GetString(baseMoney, "currency") ?? "USD",
                null,
                productUrl,
                GetString(best.Item, "coverHorizontal"),
                page.RetrievedAt,
                GetNumber(price, "discountPercent"));
        }
        catch (JsonException exception)
        {
            ReportFailure("gog", uri, exception.Message);
            return null;
        }
    }

    private async Task<StoreListingPayload?> FindEnebaListingAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(EnebaSearchUrl + Uri.EscapeDataString(game.CanonicalTitle));
        var page = await TryGetPageAsync(uri, "eneba", cancellationToken);
        if (page is null) return null;

        var scriptMatches = Regex.Matches(
            page.Body,
            """<script\b[^>]*type=["']application/ld\+json["'][^>]*>(?<json>.*?)</script>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match script in scriptMatches)
        {
            try
            {
                using var document = JsonDocument.Parse(WebUtility.HtmlDecode(script.Groups["json"].Value));
                var best = FindMatchingProduct(document.RootElement, game.CanonicalTitle)
                    .OrderByDescending(item => item.Score)
                    .FirstOrDefault();
                if (best.Name is null || best.Score < _matchingThreshold) continue;

                var offers = FindProperty(best.Item, "offers");
                var current = GetNumber(offers, "price");
                var url = GetString(best.Item, "url");
                if (current is null) continue;
                return new StoreListingPayload(
                    best.Name,
                    GetString(best.Item, "sku") ?? best.Name,
                    current,
                    GetNumber(offers, "highPrice") ?? current,
                    GetString(offers, "priceCurrency") ?? "USD",
                    null,
                    url,
                    null,
                    page.RetrievedAt);
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<SourceResponse>> FindCheapSharkListingsAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{CheapSharkDealsUrl}?title={Uri.EscapeDataString(game.CanonicalTitle)}&pageSize=20");
        var page = await TryGetPageAsync(uri, "cheapshark", cancellationToken);
        if (page is null)
        {
            RecordCheapSharkStoreRequests(successful: false);
            return Array.Empty<SourceResponse>();
        }

        try
        {
            var stores = await _cheapSharkStores.Value.WaitAsync(cancellationToken);
            RecordCheapSharkStoreRequests(successful: stores.Count > 0);
            using var document = JsonDocument.Parse(page.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<SourceResponse>();

            var storeById = stores
                .Where(entry => IsSupportedCheapSharkStore(entry.Value))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            var responses = new List<SourceResponse>();
            foreach (var deal in document.RootElement.EnumerateArray())
            {
                var storeId = GetString(deal, "storeID");
                var title = GetString(deal, "title");
                var score = title is null ? 0 : TitleMatchingStage.Similarity(game.CanonicalTitle, title);
                if (storeId is null || title is null ||
                    !storeById.TryGetValue(storeId, out var storeName) ||
                    score < _matchingThreshold)
                    continue;

                var sourceId = CheapSharkSourceId(storeName);
                if (sourceId is null || !_storeSources.Contains(sourceId)) continue;
                var listing = new StoreListingPayload(
                    title,
                    GetString(deal, "dealID") ?? string.Empty,
                    ParseDecimal(GetString(deal, "salePrice")),
                    ParseDecimal(GetString(deal, "normalPrice")),
                    "USD",
                    null,
                    GetString(deal, "dealID") is { } dealId
                        ? $"https://www.cheapshark.com/redirect?dealID={Uri.EscapeDataString(dealId)}"
                        : null,
                    GetString(deal, "thumb"),
                    page.RetrievedAt);
                if (listing.CurrentPrice is null) continue;
                responses.Add(CreateStoreResponse(game, $"cheapshark_{sourceId}", listing));
            }

            return responses;
        }
        catch (JsonException exception)
        {
            ReportFailure("cheapshark", uri, exception.Message);
            return Array.Empty<SourceResponse>();
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadCheapSharkStoresAsync(
        CancellationToken cancellationToken)
    {
        var page = await TryGetPageAsync(new Uri(CheapSharkStoresUrl), "cheapshark", cancellationToken);
        if (page is null) return new Dictionary<string, string>();
        try
        {
            using var document = JsonDocument.Parse(page.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new Dictionary<string, string>();
            return document.RootElement.EnumerateArray()
                .Select(store => (Id: GetString(store, "storeID"), Name: GetString(store, "storeName")))
                .Where(store => store.Id is not null && store.Name is not null)
                .ToDictionary(store => store.Id!, store => store.Name!, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            ReportFailure("cheapshark", new Uri(CheapSharkStoresUrl), exception.Message);
            return new Dictionary<string, string>();
        }
    }

    private static bool IsSupportedCheapSharkStore(string name)
    {
        return CheapSharkSourceId(name) is not null;
    }

    private void RecordCheapSharkStoreRequests(bool successful)
    {
        foreach (var sourceId in _storeSources.Where(IsCheapSharkSource))
        {
            var name = sourceId switch
            {
                "epicgames" => "Epic Games Store",
                "gmg" => "Green Man Gaming",
                "humble" => "Humble Store",
                "steam" => "Steam",
                "gog" => "GOG",
                "gamersgate" => "GamersGate",
                "gamebillet" => "GameBillet",
                "gamesplanet" => "Gamesplanet",
                "wingamestore" => "WinGameStore",
                "fanatical" => "Fanatical",
                "indiegala" => "IndieGala",
                "eneba" => "Eneba",
                _ => sourceId
            };
            _metrics?.RecordSourceRequest(name, successful);
        }
    }

    private static bool IsCheapSharkSource(string sourceId) => sourceId is
        "steam" or "epicgames" or "gog" or "gamersgate" or "gamebillet" or
        "gamesplanet" or "wingamestore" or "fanatical" or "gmg" or "humble" or
        "indiegala" or "eneba";

    private static string? CheapSharkSourceId(string storeName)
    {
        var normalized = Regex.Replace(storeName, "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase);
        if (normalized.Contains("greenmangaming", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("gmg", StringComparison.OrdinalIgnoreCase)) return "gmg";
        if (normalized.Contains("humble", StringComparison.OrdinalIgnoreCase)) return "humble";
        if (normalized.Contains("steam", StringComparison.OrdinalIgnoreCase)) return "steam";
        if (normalized.Equals("gog", StringComparison.OrdinalIgnoreCase)) return "gog";
        if (normalized.Contains("epicgames", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("epic", StringComparison.OrdinalIgnoreCase)) return "epicgames";
        if (normalized.Contains("gamersgate", StringComparison.OrdinalIgnoreCase)) return "gamersgate";
        if (normalized.Contains("gamebillet", StringComparison.OrdinalIgnoreCase)) return "gamebillet";
        if (normalized.Contains("gamesplanet", StringComparison.OrdinalIgnoreCase)) return "gamesplanet";
        if (normalized.Contains("wingamestore", StringComparison.OrdinalIgnoreCase)) return "wingamestore";
        if (normalized.Contains("fanatical", StringComparison.OrdinalIgnoreCase)) return "fanatical";
        if (normalized.Contains("indiegala", StringComparison.OrdinalIgnoreCase)) return "indiegala";
        if (normalized.Contains("eneba", StringComparison.OrdinalIgnoreCase)) return "eneba";
        return null;
    }

    private static IEnumerable<(JsonElement Item, string? Name, double Score)> FindMatchingProduct(
        JsonElement element,
        string title)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(element, "title") ?? GetString(element, "name");
            if (name is not null)
                yield return (element, name, TitleMatchingStage.Similarity(title, name));
            foreach (var property in element.EnumerateObject())
            {
                foreach (var match in FindMatchingProduct(property.Value, title))
                    yield return match;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var match in FindMatchingProduct(item, title))
                    yield return match;
            }
        }
    }

    private static JsonElement FindProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out var property)) return property;
            foreach (var child in element.EnumerateObject())
            {
                var found = FindProperty(child.Value, propertyName);
                if (found.ValueKind != JsonValueKind.Undefined) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var found = FindProperty(child, propertyName);
                if (found.ValueKind != JsonValueKind.Undefined) return found;
            }
        }

        return default;
    }

    private static decimal? GetNumber(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String => ParseDecimal(value.GetString()),
            _ => null
        };
    }

    private async Task<StoreListingPayload?> FindSteamListingAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        var knownAppId = game.ExternalId.StartsWith("steam:", StringComparison.Ordinal)
            ? game.ExternalId["steam:".Length..]
            : null;
        if (knownAppId is null)
        {
            var searchUri = new Uri(
                $"{SteamSearchUrl}?term={Uri.EscapeDataString(game.CanonicalTitle)}&l=spanish&cc=cr");
            var searchPage = await TryGetPageAsync(searchUri, "steam", cancellationToken);
            if (searchPage is null) return null;
            try
            {
                using var searchDocument = JsonDocument.Parse(searchPage.Body);
                if (!searchDocument.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                    return null;
                var match = items.EnumerateArray()
                    .Where(item => GetString(item, "type")?.Equals("app", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(item => new
                    {
                        Item = item,
                        Name = GetString(item, "name"),
                        Score = GetString(item, "name") is { } name
                            ? TitleMatchingStage.Similarity(game.CanonicalTitle, name)
                            : 0
                    })
                    .Where(item => item.Name is not null && item.Score >= _matchingThreshold)
                    .OrderByDescending(item => item.Score)
                    .FirstOrDefault();
                if (match?.Name is null || !match.Item.TryGetProperty("id", out var id)) return null;
                knownAppId = id.ToString();
            }
            catch (JsonException exception)
            {
                ReportFailure("steam", searchUri, exception.Message);
                return null;
            }
        }

        var uri = new Uri(
            $"{SteamAppDetailsUrl}?appids={Uri.EscapeDataString(knownAppId)}&cc=cr&l=spanish");
        var page = await TryGetPageAsync(uri, "steam", cancellationToken);
        if (page is null) return null;
        try
        {
            using var document = JsonDocument.Parse(page.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty(knownAppId, out var app) ||
                !app.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True ||
                !app.TryGetProperty("data", out var data))
            {
                return null;
            }

            var name = GetString(data, "name");
            if (name is null ||
                TitleMatchingStage.Similarity(game.CanonicalTitle, name) < _matchingThreshold)
            {
                return null;
            }

            var appId = GetString(data, "steam_appid") ?? knownAppId;
            var price = data.TryGetProperty("price_overview", out var overview)
                ? ParseSteamPrice(overview)
                : null;
            var isFree = data.TryGetProperty("is_free", out var free) &&
                free.ValueKind == JsonValueKind.True;
            return CreateSteamPayload(
                game,
                name,
                appId,
                price,
                isFree,
                GetMetacriticScore(data),
                GetString(data, "header_image"),
                GetNumber(overview, "discount_percent"),
                page.RetrievedAt);
        }
        catch (JsonException exception)
        {
            ReportFailure("steam", uri, exception.Message);
            return null;
        }
    }

    private async Task<StoreListingPayload?> FindGamersGateListingAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            GamersGateSearchUrl + "?query=" + Uri.EscapeDataString(game.CanonicalTitle));
        var page = await TryGetPageAsync(uri, "gamersgate", cancellationToken);
        if (page is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(page.Body);
            if (!document.RootElement.TryGetProperty("catalog", out var catalog) ||
                catalog.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var best = catalog.EnumerateArray()
                .Where(item =>
                    !item.TryGetProperty("is_available", out var available) ||
                    available.ValueKind != JsonValueKind.False)
                .Select(item => new
                {
                    Item = item,
                    Name = GetString(item, "name"),
                    Score = GetString(item, "name") is { } name
                        ? TitleMatchingStage.Similarity(
                            game.CanonicalTitle,
                            name)
                        : 0
                })
                .Where(item => item.Name is not null && item.Score >= _matchingThreshold)
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            if (best?.Name is null)
            {
                return null;
            }

            var currentPrice = ParseDecimal(GetString(best.Item, "raw_price")) ??
                ParsePriceText(GetString(best.Item, "price"));
            if (currentPrice is null)
            {
                return null;
            }

            var regularPrice = ParsePriceText(GetString(best.Item, "baseprice"));
            var productPath = GetString(best.Item, "link");
            var productUrl = productPath is null
                ? null
                : new Uri(new Uri("https://www.gamersgate.com"), productPath).AbsoluteUri;
            return new StoreListingPayload(
                best.Name,
                GetString(best.Item, "id") ?? string.Empty,
                currentPrice,
                regularPrice,
                GetString(best.Item, "currency_code"),
                null,
                productUrl,
                GetString(best.Item, "image"),
                page.RetrievedAt);
        }
        catch (JsonException exception)
        {
            ReportFailure("gamersgate", uri, exception.Message);
            return null;
        }
    }

    private static StoreListingPayload CreateSteamPayload(
        GameRecord game,
        string title,
        string appId,
        (decimal? Current, decimal? Regular, string? Currency)? price,
        bool isFree,
        decimal? rating,
        string? coverImageUrl,
        decimal? discountPercent,
        DateTimeOffset retrievedAt)
    {
        var productUrl = $"https://store.steampowered.com/app/{Uri.EscapeDataString(appId)}/";
        return new StoreListingPayload(
            title,
            appId,
            isFree ? 0 : price?.Current,
            isFree ? 0 : price?.Regular,
            price?.Currency ?? (isFree ? "USD" : null),
            rating,
            productUrl,
            coverImageUrl ?? game.CoverImageUrl,
            retrievedAt,
            discountPercent);
    }

    private async Task<DownloadedPage?> TryGetPageAsync(
        Uri uri,
        string sourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetPageAsync(uri, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            ReportFailure(sourceId, uri, exception.Message);
        }
        catch (IOException exception)
        {
            ReportFailure(sourceId, uri, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            ReportFailure(sourceId, uri, exception.Message);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportFailure(sourceId, uri, exception.Message);
        }

        return null;
    }

    private void ReportFailure(string sourceId, Uri uri, string reason)
    {
        if (_reportedFailures.TryAdd(sourceId, 0))
        {
            Console.Error.WriteLine(
                $"Aviso: la fuente {sourceId} no está disponible ({uri.Host}): {reason}. " +
                "Se conservarán los últimos datos válidos al publicar.");
        }
    }

    private static (decimal? Current, decimal? Regular, string? Currency)? ParseSteamPrice(
        JsonElement price)
    {
        if (price.ValueKind != JsonValueKind.Object ||
            !price.TryGetProperty("final", out var final) ||
            final.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var initial = price.TryGetProperty("initial", out var initialElement) &&
            initialElement.ValueKind == JsonValueKind.Number
                ? initialElement.GetDecimal() / 100m
                : final.GetDecimal() / 100m;
        return (
            final.GetDecimal() / 100m,
            initial,
            GetString(price, "currency"));
    }

    private static decimal? GetMetacriticScore(JsonElement data)
    {
        if (!data.TryGetProperty("metacritic", out var metacritic) ||
            metacritic.ValueKind != JsonValueKind.Object ||
            !metacritic.TryGetProperty("score", out var score))
        {
            return null;
        }

        return score.ValueKind switch
        {
            JsonValueKind.Number => score.GetDecimal(),
            JsonValueKind.String => ParseDecimal(score.GetString()),
            _ => null
        };
    }

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;

    private static decimal? ParsePriceText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = Regex.Match(value, """[-+]?\d+(?:[.,]\d{1,2})?""");
        return match.Success
            ? ParseDecimal(match.Value.Replace(',', '.'))
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record StoreListingPayload(
    string CandidateTitle,
    string ProductId,
    decimal? CurrentPrice,
    decimal? RegularPrice,
    string? Currency,
    decimal? Rating,
    string? ProductUrl,
    string? CoverImageUrl,
    DateTimeOffset RetrievedAt,
    decimal? DiscountPercent = null);

internal static class CatalogOfferRegion
{
    public static string? ForSource(string sourceId, string? productUrl = null)
    {
        if (sourceId.StartsWith("cheapshark_", StringComparison.OrdinalIgnoreCase) ||
            sourceId is "gmg" or "humble" ||
            productUrl?.Contains("cheapshark.com/redirect", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "US";
        }

        return sourceId is "steam" or "epicgames" or "gog" or "gamersgate"
            ? "CR"
            : null;
    }
}

public sealed class CatalogSourceParseStage : IParseStage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex TitleRegex = new(
        """<h1\b[^>]*>(?<title>.*?)</h1>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ScoreRegex = new(
        """average score(?: of)?\s*(?<score>[0-9]+(?:\.[0-9]+)?)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImageRegex = new(
        """<meta\s+property=["']og:image["']\s+content=["'](?<url>[^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<ParsedCandidate?> ParseAsync(
        SourceResponse response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessful || response.Body is null)
        {
            return Task.FromResult<ParsedCandidate?>(null);
        }

        if (response.SourceId.Equals("opencritic", StringComparison.OrdinalIgnoreCase))
        {
            return ParseOpenCritic(response);
        }

        try
        {
            var listing = JsonSerializer.Deserialize<StoreListingPayload>(response.Body, JsonOptions);
            if (listing is null)
            {
                return Task.FromResult<ParsedCandidate?>(null);
            }

            var isMetacritic = response.SourceId.Equals(
                "metacritic",
                StringComparison.OrdinalIgnoreCase);
            if (isMetacritic && listing.Rating is null)
            {
                return Task.FromResult<ParsedCandidate?>(null);
            }

            if (!isMetacritic &&
                listing.CurrentPrice is null &&
                listing.RegularPrice is null)
            {
                return Task.FromResult<ParsedCandidate?>(null);
            }

            return Task.FromResult<ParsedCandidate?>(new ParsedCandidate(
                response.GameId,
                response.SourceId,
                response.CanonicalTitle,
                response.Platforms,
                isMetacritic ? response.CanonicalTitle : listing.CandidateTitle,
                isMetacritic ? null : response.ProductUrl ?? listing.ProductUrl,
                isMetacritic ? null : listing.CurrentPrice,
                isMetacritic ? null : listing.RegularPrice,
                isMetacritic ? null : listing.Currency,
                isMetacritic ? OfferFormat.Unknown : OfferFormat.Digital,
                isMetacritic ? listing.Rating : null,
                isMetacritic ? "Metacritic" : null,
                null,
                null,
                null,
                listing.CoverImageUrl,
                listing.RetrievedAt,
                response.RepositoryMsrp,
                listing.DiscountPercent));
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine(
                $"Aviso: respuesta JSON inválida de {response.SourceId} para " +
                $"{response.CanonicalTitle}: {exception.Message}");
            return Task.FromResult<ParsedCandidate?>(null);
        }
    }

    private static Task<ParsedCandidate?> ParseOpenCritic(SourceResponse response)
    {
        if (response.Body is null)
        {
            return Task.FromResult<ParsedCandidate?>(null);
        }

        var titleMatch = TitleRegex.Match(response.Body);
        if (!titleMatch.Success)
        {
            Console.Error.WriteLine(
                $"Aviso: OpenCritic no devolvió un título para {response.CanonicalTitle}.");
            return Task.FromResult<ParsedCandidate?>(null);
        }

        var title = CleanHtml(titleMatch.Groups["title"].Value);
        decimal? rating = null;
        var scoreMatch = ScoreRegex.Match(WebUtility.HtmlDecode(response.Body));
        if (scoreMatch.Success &&
            decimal.TryParse(
                scoreMatch.Groups["score"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsedScore))
        {
            rating = parsedScore;
        }

        var imageMatch = ImageRegex.Match(response.Body);
        var image = imageMatch.Success
            ? WebUtility.HtmlDecode(imageMatch.Groups["url"].Value)
            : null;

        return Task.FromResult<ParsedCandidate?>(new ParsedCandidate(
            response.GameId,
            response.SourceId,
            response.CanonicalTitle,
            response.Platforms,
            title,
            response.ProductUrl,
            null,
            null,
            null,
            OfferFormat.Unknown,
            rating,
            "OpenCritic",
            null,
            null,
            null,
            image,
            response.RetrievedAt,
            response.RepositoryMsrp));
    }

    private static string CleanHtml(string value) =>
        WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", string.Empty)).Trim();
}

public sealed class TitleMatchingStage : IMatchStage
{
    private static readonly Regex EditionSuffixRegex = new(
        """(?:\s+(?:remastered|remaster|definitive(?:\s+edition)?|deluxe(?:\s+edition)?|goty|game\s+of\s+the\s+year(?:\s+edition)?|complete(?:\s+edition)?|ultimate(?:\s+edition)?|enhanced(?:\s+edition)?|edition))+$""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PlatformSuffixRegex = new(
        """(?:\s+(?:for\s+)?(?:pc|steam|ps[345]|playstation(?:\s+[345])?|xbox(?:\s+(?:one|series\s+[xs]))?|nintendo\s+switch|switch))+$""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PunctuationRegex = new(
        """[^\p{L}\p{N}\s]""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ApostropheRegex = new(
        """['’]""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRegex = new(
        """\s+""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InstallmentRegex = new(
        """(?:^|\s)(?<installment>[0-9]+|II|III|IV|V|VI|VII|VIII|IX|X)$""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DifficultCaseRegex = new(
        """\b(remaster(?:ed)?|remake|definitive|deluxe|goty|edition|dlc|expansion|season pass|bundle|pack|collection|II|III|IV|V|VI|VII|VIII|IX|X)\b""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NumberWordRegex = new(
        """\b(zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty)\b""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, string> NumberWords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3",
            ["four"] = "4", ["five"] = "5", ["six"] = "6", ["seven"] = "7",
            ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["eleven"] = "11",
            ["twelve"] = "12", ["thirteen"] = "13", ["fourteen"] = "14",
            ["fifteen"] = "15", ["sixteen"] = "16", ["seventeen"] = "17",
            ["eighteen"] = "18", ["nineteen"] = "19", ["twenty"] = "20"
        };

    private readonly double _threshold;
    private readonly DealRule _dealRule;
    private readonly TitleCandidatePool _titleCandidates;
    private int _maxDegreeOfParallelism = 1;

    public TitleMatchingStage(
        double threshold,
        DealRule dealRule,
        TitleCandidatePool titleCandidates)
    {
        _threshold = threshold;
        _dealRule = dealRule;
        _titleCandidates = titleCandidates;
    }

    public void ConfigureParallelism(int maxDegreeOfParallelism)
    {
        if (maxDegreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        }

        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    public Task<MatchedOffer?> MatchAsync(
        ParsedCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var title = Normalize(candidate.CandidateTitle);
        var candidates = _titleCandidates.Snapshot()
            .Append(new TitleMatchCandidate(
                candidate.GameId,
                candidate.CanonicalTitle,
                DifficultCaseRegex.IsMatch(candidate.CanonicalTitle)
                    ? new[] { "caso_dificil" }
                    : Array.Empty<string>()))
            .DistinctBy(item => item.GameId)
            .ToArray();
        var scored = new System.Collections.Concurrent.ConcurrentBag<(TitleMatchCandidate Candidate, double Score)>();
        Parallel.ForEach(
            candidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maxDegreeOfParallelism
            },
            catalogCandidate =>
            {
                var score = Similarity(title, Normalize(catalogCandidate.CanonicalTitle));
                scored.Add((catalogCandidate, score));
            });

        var ranked = scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.GameId)
            .Take(2)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Score < _threshold)
        {
            return Task.FromResult<MatchedOffer?>(null);
        }

        var best = ranked[0];
        var ambiguous = ranked.Length > 1 && best.Score - ranked[1].Score < 0.03;
        var (isDeal, discountPercent) = CalculateDeal(candidate);
        return Task.FromResult<MatchedOffer?>(new MatchedOffer(
            best.Candidate.GameId,
            best.Candidate.GameId == candidate.GameId
                ? candidate.CanonicalTitle
                : best.Candidate.CanonicalTitle,
            candidate,
            best.Score,
            best.Score - _threshold < 0.05 ||
                ambiguous ||
                best.Candidate.DifficultCaseTags.Count > 0,
            isDeal,
            discountPercent));
    }

    public static MatchingValidationReport Validate(
        IReadOnlyList<MatchingValidationPair> pairs,
        double threshold)
    {
        if (pairs.Count < 60)
        {
            throw new InvalidDataException(
                $"La validación de emparejamiento requiere al menos 60 pares; recibió {pairs.Count}.");
        }

        var truePositives = 0;
        var falsePositives = 0;
        var falseNegatives = 0;
        var misclassified = new List<MatchingValidationError>();
        foreach (var pair in pairs)
        {
            var similarity = Similarity(pair.Left, pair.Right);
            var predictedMatch = similarity >= threshold;
            if (predictedMatch && pair.ExpectedMatch)
            {
                truePositives++;
            }
            else if (predictedMatch)
            {
                falsePositives++;
            }
            else if (pair.ExpectedMatch)
            {
                falseNegatives++;
            }

            if (predictedMatch != pair.ExpectedMatch)
            {
                misclassified.Add(new MatchingValidationError(
                    pair.Left,
                    pair.Right,
                    pair.ExpectedMatch,
                    predictedMatch,
                    similarity));
            }
        }

        var precision = truePositives + falsePositives == 0
            ? 0
            : (double)truePositives / (truePositives + falsePositives);
        var recall = truePositives + falseNegatives == 0
            ? 0
            : (double)truePositives / (truePositives + falseNegatives);
        return new MatchingValidationReport(
            pairs.Count,
            truePositives,
            falsePositives,
            falseNegatives,
            precision,
            recall,
            misclassified);
    }

    public static double Similarity(string left, string right)
    {
        left = Normalize(NormalizeNumberWords(left));
        right = Normalize(NormalizeNumberWords(right));
        if (left.Length == 0 || right.Length == 0)
        {
            return left == right ? 1 : 0;
        }

        var leftInstallment = GetInstallment(left);
        var rightInstallment = GetInstallment(right);
        if (leftInstallment != rightInstallment &&
            (leftInstallment is not null || rightInstallment is not null))
        {
            return 0.74;
        }

        var editSimilarity = LevenshteinSimilarity(left, right);
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var diceSimilarity = 2d * leftTokens.Intersect(rightTokens).Count() /
            (leftTokens.Count + rightTokens.Count);
        return (editSimilarity * 0.7) + (diceSimilarity * 0.3);
    }

    public static string NormalizeNumberWords(string title) =>
        NumberWordRegex.Replace(title, match => NumberWords[match.Value]);

    private (bool IsDeal, decimal? DiscountPercent) CalculateDeal(ParsedCandidate candidate)
    {
        var currentPrice = candidate.CurrentPrice;
        if (candidate.SourceId is "steam" or "epicgames" or "gog" or "gamersgate")
        {
            var localRegularPrice = candidate.RegularPrice;
            if (currentPrice is null || localRegularPrice is not > 0 ||
                localRegularPrice.Value <= currentPrice.Value)
            {
                return (false, null);
            }

            if (candidate.ReportedDiscountPercent is { } reportedLocalDiscount)
            {
                return reportedLocalDiscount > 0
                    ? (true, reportedLocalDiscount)
                    : (false, null);
            }

            var localDiscount = Math.Round(
                100m * (localRegularPrice.Value - currentPrice.Value) / localRegularPrice.Value,
                2,
                MidpointRounding.AwayFromZero);
            return (localDiscount > 0, localDiscount > 0 ? localDiscount : null);
        }

        var referencePrice = _dealRule switch
        {
            DealRule.RepositoryMsrp => candidate.RepositoryMsrp,
            DealRule.StoreRegularPrice => candidate.RegularPrice,
            DealRule.Combined when currentPrice is not null &&
                candidate.RegularPrice is > 0 &&
                candidate.RegularPrice.Value > currentPrice.Value =>
                candidate.RegularPrice,
            DealRule.Combined when currentPrice is not null &&
                candidate.RepositoryMsrp is > 0 &&
                candidate.RepositoryMsrp.Value > currentPrice.Value =>
                candidate.RepositoryMsrp,
            _ => null
        };
        if (currentPrice is null || referencePrice is null || referencePrice <= 0)
        {
            return (false, null);
        }

        if (candidate.ReportedDiscountPercent is > 0 && currentPrice < referencePrice)
        {
            return (true, candidate.ReportedDiscountPercent);
        }

        var discount = Math.Round(
            100m * (referencePrice.Value - currentPrice.Value) / referencePrice.Value,
            2,
            MidpointRounding.AwayFromZero);
        return discount > 0
            ? (true, discount)
            : (false, null);
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var withoutDiacritics = Regex.Replace(decomposed, @"\p{Mn}", string.Empty);
        var normalized = ApostropheRegex.Replace(withoutDiacritics.ToLowerInvariant(), string.Empty);
        normalized = PunctuationRegex.Replace(normalized, " ");
        normalized = WhitespaceRegex.Replace(normalized, " ").Trim();
        normalized = PlatformSuffixRegex.Replace(normalized, string.Empty);
        normalized = EditionSuffixRegex.Replace(normalized, string.Empty);
        normalized = WhitespaceRegex.Replace(normalized, " ").Trim();
        var installment = InstallmentRegex.Match(normalized);
        if (installment.Success &&
            TryGetRomanValue(installment.Groups["installment"].Value, out var valueNumber))
        {
            normalized = normalized[..installment.Groups["installment"].Index] + valueNumber;
        }

        return normalized;
    }

    private static string? GetInstallment(string title)
    {
        var match = InstallmentRegex.Match(title);
        if (!match.Success)
        {
            return null;
        }

        return TryGetRomanValue(match.Groups["installment"].Value, out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : match.Groups["installment"].Value;
    }

    private static bool TryGetRomanValue(string value, out int number)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = value.ToUpperInvariant() switch
        {
            "II" => 2,
            "III" => 3,
            "IV" => 4,
            "V" => 5,
            "VI" => 6,
            "VII" => 7,
            "VIII" => 8,
            "IX" => 9,
            "X" => 10,
            _ => 0
        };
        return number != 0;
    }

    private static double LevenshteinSimilarity(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return 1d - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }
}

public sealed class SqlitePersistStage : IPersistStage
{
    private readonly SqliteCatalogRepository _repository;

    public SqlitePersistStage(SqliteCatalogRepository repository)
    {
        _repository = repository;
    }

    public async Task<PersistedOffer?> PersistAsync(
        MatchedOffer offer,
        CancellationToken cancellationToken)
    {
        var candidate = offer.Candidate;
        var ratingObservationId = candidate.Rating is { } rating
            ? await _repository.SaveRatingAsync(
                offer.GameId,
                offer.MatchedCanonicalTitle,
                candidate.SourceId,
                rating,
                candidate.RetrievedAt,
                cancellationToken)
            : 0;
        var priceObservationId = candidate.CurrentPrice is not null
            ? await _repository.SavePriceObservationAsync(
                offer.GameId,
                offer.MatchedCanonicalTitle,
                candidate.SourceId,
                candidate.ProductUrl,
                candidate.CurrentPrice,
                candidate.RegularPrice,
                candidate.Currency,
                candidate.Format,
                offer.IsDeal,
                offer.DiscountPercent,
                candidate.RetrievedAt,
                cancellationToken)
            : 0;
        if (candidate.MainStoryMinutes is not null ||
            candidate.StoryAndExtrasMinutes is not null ||
            candidate.CompletionistMinutes is not null)
        {
            await _repository.SavePlaytimeObservationAsync(
                offer.GameId,
                offer.MatchedCanonicalTitle,
                "howlongtobeat",
                candidate.MainStoryMinutes,
                candidate.StoryAndExtrasMinutes,
                candidate.CompletionistMinutes,
                candidate.RetrievedAt,
                cancellationToken);
        }

        return new PersistedOffer(
            priceObservationId != 0 ? priceObservationId : ratingObservationId,
            offer.GameId,
            candidate.SourceId,
            offer.MatchedCanonicalTitle,
            candidate.ProductUrl,
            candidate.CurrentPrice,
            candidate.RegularPrice,
            candidate.Currency,
            candidate.Format,
            offer.IsDeal,
            offer.DiscountPercent,
            candidate.Rating,
            candidate.CoverImageUrl,
            candidate.Platforms,
            candidate.RetrievedAt,
            offer.Similarity,
            offer.IsUncertain,
            Region: CatalogOfferRegion.ForSource(candidate.SourceId, candidate.ProductUrl));
    }
}

public sealed class JsonPublishStage : IPublishStage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _catalogPath;
    private readonly string _historyPath;
    private readonly SqliteCatalogRepository _repository;
    private readonly CatalogSeedCatalog _seedCatalog;
    private readonly List<PersistedOffer> _offers = new();
    private readonly SemaphoreSlim _sync = new(1, 1);

    public JsonPublishStage(
        SqliteCatalogRepository repository,
        CatalogSeedCatalog seedCatalog)
    {
        _repository = repository;
        _seedCatalog = seedCatalog;
        _catalogPath = Path.GetFullPath(Path.Combine("web", "data", "catalog.json"));
        _historyPath = Path.GetFullPath(Path.Combine("web", "data", "history.json"));
    }

    public async Task PublishAsync(
        PersistedOffer offer,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            _offers.Add(offer);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        var metadata = await _repository.ReadCatalogMetadataAsync(cancellationToken);
        var previousCatalog = await ReadPreviousCatalogAsync(cancellationToken);
        var knownGameIds = metadata
            .Select(game => game.GameId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousById = previousCatalog.ToDictionary(
            game => game.GameId,
            StringComparer.OrdinalIgnoreCase);
        metadata = metadata
            .Select(game => previousById.TryGetValue(game.GameId, out var previous)
                ? game with
                {
                    Msrp = game.Msrp ?? previous.Msrp,
                    Currency = game.Currency ?? previous.Currency,
                    CoverImageUrl = game.CoverImageUrl ?? previous.CoverImageUrl,
                    OpenCriticScore = game.OpenCriticScore ?? previous.OpenCriticScore,
                    MetacriticScore = game.MetacriticScore ?? previous.MetacriticScore,
                    MainStoryMinutes = game.MainStoryMinutes ?? previous.MainStoryMinutes,
                    StoryAndExtrasMinutes =
                        game.StoryAndExtrasMinutes ?? previous.StoryAndExtrasMinutes,
                    CompletionistMinutes = game.CompletionistMinutes ?? previous.CompletionistMinutes,
                    PlaytimeCapturedAt = game.PlaytimeCapturedAt ?? previous.CapturedAt,
                    ShortDescription = game.ShortDescription ?? previous.ShortDescription,
                    Genres = game.Genres is { Count: > 0 } ? game.Genres : previous.Genres,
                    Developer = game.Developer ?? previous.Developer,
                    MainStoryEstimated = game.MainStoryMinutes is null
                        ? previous.MainStoryEstimated
                        : game.MainStoryEstimated,
                    StoryAndExtrasEstimated = game.StoryAndExtrasMinutes is null
                        ? previous.StoryAndExtrasEstimated
                        : game.StoryAndExtrasEstimated,
                    CompletionistEstimated = game.CompletionistMinutes is null
                        ? previous.CompletionistEstimated
                        : game.CompletionistEstimated
                }
                : game)
            .Concat(previousCatalog
                .Where(game => !knownGameIds.Contains(game.GameId))
                .Select(game => new CatalogGameMetadata(
                    game.GameId,
                    game.CanonicalTitle,
                    game.Type,
                    game.ReleaseDate,
                    game.Msrp,
                    game.Currency,
                    game.Platforms,
                    game.CoverImageUrl,
                    game.OpenCriticScore,
                    game.MetacriticScore,
                    game.MainStoryMinutes,
                    game.StoryAndExtrasMinutes,
                    game.CompletionistMinutes,
                    game.CapturedAt,
                    game.ShortDescription,
                    game.Genres,
                    game.Developer,
                    game.MainStoryEstimated,
                    game.StoryAndExtrasEstimated,
                    game.CompletionistEstimated)))
            .ToArray();
        metadata = metadata
            .Select(game =>
            {
                var estimate = _seedCatalog.EstimatePlaytime(
                    game.CanonicalTitle,
                    game.Type,
                    game.Genres ?? Array.Empty<string>());
                if (estimate is null)
                {
                    return game;
                }

                return game with
                {
                    MainStoryMinutes = game.MainStoryMinutes ?? estimate.MainStoryMinutes,
                    StoryAndExtrasMinutes =
                        game.StoryAndExtrasMinutes ?? estimate.StoryAndExtrasMinutes,
                    CompletionistMinutes =
                        game.CompletionistMinutes ?? estimate.CompletionistMinutes,
                    MainStoryEstimated = game.MainStoryMinutes is null || game.MainStoryEstimated,
                    StoryAndExtrasEstimated =
                        game.StoryAndExtrasMinutes is null || game.StoryAndExtrasEstimated,
                    CompletionistEstimated =
                        game.CompletionistMinutes is null || game.CompletionistEstimated
                };
            })
            .ToArray();

        var priceHistory = (await _repository.ReadPriceHistoryAsync(cancellationToken)).ToList();
        var historyKeys = priceHistory
            .Select(HistoryKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var oldObservation in await ReadPreviousHistoryAsync(cancellationToken))
        {
            if (oldObservation.CurrentPrice is not null &&
                historyKeys.Add(HistoryKey(oldObservation)))
            {
                priceHistory.Add(oldObservation);
            }
        }

        foreach (var oldOffer in previousCatalog.SelectMany(game => game.Offers))
        {
            if (oldOffer.CurrentPrice is null)
            {
                continue;
            }

            var historyEntry = new PriceHistoryEntry(
                oldOffer.ObservationId,
                oldOffer.GameId.ToString("D"),
                oldOffer.SourceId,
                oldOffer.CurrentPrice,
                oldOffer.Currency,
                oldOffer.Format,
                oldOffer.CapturedAt,
                oldOffer.ProductUrl,
                oldOffer.RegularPrice,
                oldOffer.IsDeal,
                oldOffer.DiscountPercent);
            if (historyKeys.Add(HistoryKey(historyEntry)))
            {
                priceHistory.Add(historyEntry);
            }
        }

        var metadataById = metadata.ToDictionary(
            game => game.GameId,
            StringComparer.OrdinalIgnoreCase);
        priceHistory = priceHistory.Select(entry =>
        {
            var regularPrice = ResolveRegularPrice(entry, metadataById);
            var discountPercent = CalculateDiscountPercent(entry.CurrentPrice, regularPrice);
            return entry with
            {
                RegularPrice = regularPrice,
                IsDeal = discountPercent is not null,
                DiscountPercent = discountPercent
            };
        }).ToList();
        var historicalMinimums = priceHistory
            .Where(entry => entry.CurrentPrice is not null && !entry.IsEstimated)
            .GroupBy(entry => $"{entry.GameId}|{entry.SourceId}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Min(entry => entry.CurrentPrice!.Value),
                StringComparer.OrdinalIgnoreCase);
        var offersByGame = priceHistory
            .Where(entry => entry.CurrentPrice is not null && !entry.IsEstimated)
            .GroupBy(entry => $"{entry.GameId}|{entry.SourceId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => entry.CapturedAt)
                .ThenByDescending(entry => entry.ObservationId)
                .First())
            .GroupBy(entry => entry.GameId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PersistedOffer>)group.Select(entry =>
                {
                    var sourceKey = $"{entry.GameId}|{entry.SourceId}";
                    var age = DateTimeOffset.UtcNow - entry.CapturedAt;
                    var isStale = age > TimeSpan.FromHours(24);
                    return new PersistedOffer(
                        entry.ObservationId,
                        Guid.Parse(entry.GameId),
                        entry.SourceId,
                        string.Empty,
                        entry.ProductUrl,
                        entry.CurrentPrice,
                        entry.RegularPrice,
                        entry.Currency,
                        entry.Format,
                        !isStale && entry.IsDeal,
                        entry.DiscountPercent,
                        null,
                        null,
                        Array.Empty<string>(),
                        entry.CapturedAt,
                        1,
                        false,
                        !isStale && historicalMinimums[sourceKey] == entry.CurrentPrice,
                        isStale,
                        isStale ? entry.CapturedAt : null,
                        CatalogOfferRegion.ForSource(entry.SourceId, entry.ProductUrl));
                }).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var historyByGame = priceHistory
            .Where(entry => entry.CurrentPrice is not null)
            .GroupBy(entry => entry.GameId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PriceHistoryEntry>)group
                    .OrderBy(entry => entry.CapturedAt)
                    .ThenBy(entry => entry.ObservationId)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var capturedAt = DateTimeOffset.UtcNow;
        var catalog = metadata
            .GroupBy(game => NormalizeTitle(game.CanonicalTitle), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var entries = group.ToArray();
                var primary = entries
                    .OrderByDescending(entry => entry.OpenCriticScore.HasValue)
                    .ThenByDescending(entry => !string.IsNullOrEmpty(entry.CoverImageUrl))
                    .First();
                var offers = entries
                    .SelectMany(entry =>
                        offersByGame.TryGetValue(entry.GameId, out var gameOffers)
                            ? gameOffers
                            : Array.Empty<PersistedOffer>())
                    .ToArray();
                var gameHistory = entries
                    .SelectMany(entry =>
                        historyByGame.TryGetValue(entry.GameId, out var gameHistoryEntries)
                            ? gameHistoryEntries
                            : Array.Empty<PriceHistoryEntry>())
                    .ToArray();
                return new CatalogExportEntry(
                    primary.GameId,
                    primary.CanonicalTitle,
                    primary.Type,
                    entries.Select(entry => entry.ReleaseDate).FirstOrDefault(value => value is not null),
                    entries.Select(entry => entry.Msrp).FirstOrDefault(value => value is not null),
                    entries.Select(entry => entry.Currency).FirstOrDefault(value => value is not null),
                    entries.SelectMany(entry => entry.Platforms)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(platform => platform, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    entries.Select(entry => entry.CoverImageUrl)
                        .FirstOrDefault(value => !string.IsNullOrEmpty(value)),
                    entries.Select(entry => entry.OpenCriticScore)
                        .FirstOrDefault(value => value is not null),
                    entries.Select(entry => entry.MetacriticScore)
                        .FirstOrDefault(value => value is not null),
                    entries.OrderBy(entry => entry.MainStoryEstimated)
                        .Select(entry => entry.MainStoryMinutes)
                        .FirstOrDefault(value => value is not null),
                    entries.OrderBy(entry => entry.StoryAndExtrasEstimated)
                        .Select(entry => entry.StoryAndExtrasMinutes)
                        .FirstOrDefault(value => value is not null),
                    entries.OrderBy(entry => entry.CompletionistEstimated)
                        .Select(entry => entry.CompletionistMinutes)
                        .FirstOrDefault(value => value is not null),
                    capturedAt,
                    offers.Any(offer => offer.IsHistoricalLow),
                    offers,
                    gameHistory,
                    entries.Select(entry => entry.ShortDescription)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    entries.SelectMany(entry => entry.Genres ?? Array.Empty<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    entries.Select(entry => entry.Developer)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    entries.OrderBy(entry => entry.MainStoryEstimated)
                        .Where(entry => entry.MainStoryMinutes is not null)
                        .Select(entry => entry.MainStoryEstimated)
                        .FirstOrDefault(true),
                    entries.OrderBy(entry => entry.StoryAndExtrasEstimated)
                        .Where(entry => entry.StoryAndExtrasMinutes is not null)
                        .Select(entry => entry.StoryAndExtrasEstimated)
                        .FirstOrDefault(true),
                    entries.OrderBy(entry => entry.CompletionistEstimated)
                        .Where(entry => entry.CompletionistMinutes is not null)
                        .Select(entry => entry.CompletionistEstimated)
                        .FirstOrDefault(true));
            }).ToArray();

        await WriteJsonAsync(_historyPath, priceHistory, cancellationToken);
        await WriteJsonAsync(_catalogPath, catalog, cancellationToken);
    }

    private async Task<CatalogExportEntry[]> ReadPreviousCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
        {
            return Array.Empty<CatalogExportEntry>();
        }

        await using var stream = File.OpenRead(_catalogPath);
        return await JsonSerializer.DeserializeAsync<CatalogExportEntry[]>(
            stream,
            JsonOptions,
            cancellationToken) ?? Array.Empty<CatalogExportEntry>();
    }

    private async Task<PriceHistoryEntry[]> ReadPreviousHistoryAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_historyPath))
        {
            return Array.Empty<PriceHistoryEntry>();
        }

        await using var stream = File.OpenRead(_historyPath);
        return await JsonSerializer.DeserializeAsync<PriceHistoryEntry[]>(
            stream,
            JsonOptions,
            cancellationToken) ?? Array.Empty<PriceHistoryEntry>();
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                value,
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string NormalizeTitle(string title) =>
        Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static string HistoryKey(PriceHistoryEntry entry) =>
        $"{entry.GameId}|{entry.SourceId}|{entry.CapturedAt:O}|{entry.CurrentPrice}|{entry.IsEstimated}";

    private static decimal? ResolveRegularPrice(
        PriceHistoryEntry entry,
        IReadOnlyDictionary<string, CatalogGameMetadata> metadataById)
    {
        if (entry.RegularPrice is > 0) return entry.RegularPrice;
        if (!metadataById.TryGetValue(entry.GameId, out var game) ||
            game.Msrp is not > 0 ||
            string.IsNullOrWhiteSpace(entry.Currency) ||
            !string.Equals(entry.Currency, game.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return entry.RegularPrice;
        }

        return game.Msrp;
    }

    private static decimal? CalculateDiscountPercent(decimal? currentPrice, decimal? regularPrice)
    {
        if (currentPrice is null || regularPrice is not > 0 || currentPrice >= regularPrice)
            return null;
        var discount = Math.Round(
            100m * (regularPrice.Value - currentPrice.Value) / regularPrice.Value,
            2,
            MidpointRounding.AwayFromZero);
        return discount > 0 ? discount : null;
    }

    private sealed record CatalogExportEntry(
        string GameId,
        string CanonicalTitle,
        string Type,
        string? ReleaseDate,
        decimal? Msrp,
        string? Currency,
        IReadOnlyList<string> Platforms,
        string? CoverImageUrl,
        decimal? OpenCriticScore,
        decimal? MetacriticScore,
        int? MainStoryMinutes,
        int? StoryAndExtrasMinutes,
        int? CompletionistMinutes,
        DateTimeOffset CapturedAt,
        bool IsHistoricalLow,
        IReadOnlyList<PersistedOffer> Offers,
        IReadOnlyList<PriceHistoryEntry> History,
        string? ShortDescription = null,
        IReadOnlyList<string>? Genres = null,
        string? Developer = null,
        bool MainStoryEstimated = false,
        bool StoryAndExtrasEstimated = false,
        bool CompletionistEstimated = false)
    {
        [JsonPropertyName("sinopsis")]
        public string? Sinopsis => ShortDescription;

        [JsonPropertyName("mejorOferta")]
        public PersistedOffer? MejorOferta => Offers
            .Where(offer => offer.Region == "CR" && !offer.IsStale && offer.CurrentPrice is not null)
            .OrderBy(offer => offer.CurrentPrice)
            .FirstOrDefault();

        [JsonPropertyName("generos")]
        public string Generos => string.Join(", ", Genres ?? Array.Empty<string>());

        [JsonPropertyName("desarrollador")]
        public string? Desarrollador => Developer;

        [JsonPropertyName("lanzamiento")]
        public string? Lanzamiento => ReleaseDate;
    }
}

public static class ChannelStageWorkers
{
    public static async Task RunDownloadWorkersAsync(
        ChannelReader<GameRecord> input,
        ChannelWriter<SourceResponse> output,
        IDownloadStage stage,
        CancellationToken cancellationToken)
    {
        await foreach (var game in input.ReadAllAsync(cancellationToken))
        {
            foreach (var response in await stage.DownloadAsync(game, cancellationToken))
            {
                await output.WriteAsync(response, cancellationToken);
            }
        }
    }

    public static async Task RunParseWorkerAsync(
        ChannelReader<SourceResponse> input,
        ChannelWriter<ParsedCandidate> output,
        IParseStage stage,
        CancellationToken cancellationToken)
    {
        await foreach (var response in input.ReadAllAsync(cancellationToken))
        {
            var candidate = await stage.ParseAsync(response, cancellationToken);
            if (candidate is not null)
            {
                await output.WriteAsync(candidate, cancellationToken);
            }
        }
    }

    public static async Task RunMatchWorkerAsync(
        ChannelReader<ParsedCandidate> input,
        ChannelWriter<MatchedOffer> output,
        IMatchStage stage,
        PipelineRunMetrics metrics,
        CancellationToken cancellationToken)
    {
        await foreach (var candidate in input.ReadAllAsync(cancellationToken))
        {
            var matched = await stage.MatchAsync(candidate, cancellationToken);
            metrics.RecordMatch(candidate.GameId, matched);
            if (matched is not null)
            {
                await output.WriteAsync(matched, cancellationToken);
            }
        }
    }

    public static async Task RunPersistWorkerAsync(
        ChannelReader<MatchedOffer> input,
        ChannelWriter<PersistedOffer> output,
        IPersistStage stage,
        CancellationToken cancellationToken)
    {
        await foreach (var offer in input.ReadAllAsync(cancellationToken))
        {
            var persisted = await stage.PersistAsync(offer, cancellationToken);
            if (persisted is not null)
            {
                await output.WriteAsync(persisted, cancellationToken);
            }
        }
    }

    public static async Task RunPublishWorkerAsync(
        ChannelReader<PersistedOffer> input,
        IPublishStage stage,
        CancellationToken cancellationToken)
    {
        await foreach (var offer in input.ReadAllAsync(cancellationToken))
        {
            await stage.PublishAsync(offer, cancellationToken);
        }

        await stage.CompleteAsync(cancellationToken);
    }
}
