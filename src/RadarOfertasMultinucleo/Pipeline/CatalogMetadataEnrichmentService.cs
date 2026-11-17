using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;
using RadarOfertasMultinucleo.Infrastructure.Database;

namespace RadarOfertasMultinucleo.Pipeline;

public sealed class CatalogMetadataEnrichmentService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(30);
    private static readonly Regex SteamAppIdRegex = new(
        """(?:steam:|/app/)(?<id>\d+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YearRegex = new(
        """\b(?:19|20)\d{2}\b""",
        RegexOptions.Compiled);
    private readonly SqliteCatalogRepository _repository;
    private readonly CatalogSeedCatalog _seedCatalog;
    private readonly HttpClient _client;
    private readonly DomainRequestGate _steamGate;
    private readonly DomainRequestGate _gogGate;
    private readonly DomainRequestGate _howLongToBeatGate;
    private readonly ConcurrentDictionary<string, byte> _reportedFailures =
        new(StringComparer.OrdinalIgnoreCase);

    public CatalogMetadataEnrichmentService(
        SqliteCatalogRepository repository,
        CatalogSeedCatalog seedCatalog,
        PipelineOptions options)
    {
        _repository = repository;
        _seedCatalog = seedCatalog;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "RadarOfertasMultinucleo/1.0 (+https://github.com/zzetta0507-lgtm/radar-ofertas-multinucleo)");
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        _steamGate = new DomainRequestGate(
            options.MaxConcurrentRequestsPerDomain,
            options.DomainMinIntervalMilliseconds);
        _gogGate = new DomainRequestGate(
            options.MaxConcurrentRequestsPerDomain,
            options.DomainMinIntervalMilliseconds);
        _howLongToBeatGate = new DomainRequestGate(
            options.MaxConcurrentRequestsPerDomain,
            options.DomainMinIntervalMilliseconds);
    }

    public async Task<CatalogEnrichmentResult> EnrichAsync(
        bool allowNetwork,
        int maxGames,
        CancellationToken cancellationToken,
        bool enrichPlaytime = true)
    {
        var metadata = await _repository.ReadCatalogMetadataAsync(cancellationToken);
        var targets = await _repository.ReadMetadataEnrichmentTargetsAsync(cancellationToken);
        var metadataById = metadata.ToDictionary(game => game.GameId, StringComparer.OrdinalIgnoreCase);
        var estimatedCount = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var target in targets)
        {
            if (!metadataById.TryGetValue(target.GameId.ToString("D"), out var game))
            {
                continue;
            }

            var estimate = _seedCatalog.EstimatePlaytime(
                game.CanonicalTitle,
                game.Type,
                game.Genres ?? Array.Empty<string>());
            if (estimate is null)
            {
                continue;
            }

            var main = game.MainStoryEstimated || game.MainStoryMinutes is null
                ? estimate.MainStoryMinutes
                : game.MainStoryMinutes.Value;
            var extras = game.StoryAndExtrasEstimated || game.StoryAndExtrasMinutes is null
                ? estimate.StoryAndExtrasMinutes
                : game.StoryAndExtrasMinutes.Value;
            var completionist =
                game.CompletionistEstimated || game.CompletionistMinutes is null
                    ? estimate.CompletionistMinutes
                    : game.CompletionistMinutes.Value;
            if (main == game.MainStoryMinutes &&
                extras == game.StoryAndExtrasMinutes &&
                completionist == game.CompletionistMinutes)
            {
                continue;
            }

            await _repository.SavePlaytimeObservationAsync(
                target.GameId,
                target.CanonicalTitle,
                "howlongtobeat",
                main,
                extras,
                completionist,
                now,
                cancellationToken,
                isEstimated: true);
            estimatedCount++;
        }

        if (!allowNetwork)
        {
            return new CatalogEnrichmentResult(targets.Count, 0, 0, estimatedCount);
        }

        var dueTargets = targets
            .Where(target => metadataById.TryGetValue(target.GameId.ToString("D"), out var game) &&
                ((HasMissingNarrativeMetadata(game) && IsDue(target.MetadataCheckedAt, now)) ||
                 (enrichPlaytime && HasMissingPlaytime(game) &&
                  IsDue(target.PlaytimeCheckedAt, now))))
            .Take(Math.Max(0, maxGames))
            .ToArray();
        var metadataCount = 0;
        var howLongToBeatCount = 0;

        await Parallel.ForEachAsync(
            dueTargets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, _steamGate.ConcurrencyLimit))
            },
            async (target, token) =>
            {
                if (metadataById.TryGetValue(target.GameId.ToString("D"), out var game))
                {
                    if (HasMissingNarrativeMetadata(game) && IsDue(target.MetadataCheckedAt, now))
                    {
                        var steam = TryGetSteamAppId(target.SteamAppId) is { } appId
                            ? await FetchSteamMetadataAsync(appId, target.CanonicalTitle, token)
                            : null;
                        var gog = HasMissingNarrativeMetadata(steam)
                            ? await FetchGogMetadataAsync(target.CanonicalTitle, token)
                            : null;
                        var seed = _seedCatalog.FindMetadata(target.CanonicalTitle);
                        var merged = MergeMetadata(steam, gog, seed);
                        await _repository.SaveGameNarrativeMetadataAsync(
                            target.GameId,
                            merged.ShortDescription,
                            merged.Genres,
                            merged.Developer,
                            merged.ReleaseDate,
                            DateTimeOffset.UtcNow,
                            token);
                        if (merged.HasValues) Interlocked.Increment(ref metadataCount);
                    }

                    if (enrichPlaytime &&
                        HasMissingPlaytime(game) &&
                        IsDue(target.PlaytimeCheckedAt, now))
                    {
                        var playtime = await FetchHowLongToBeatAsync(
                            target.CanonicalTitle,
                            token);
                        if (playtime is not null)
                        {
                            await _repository.SavePlaytimeObservationAsync(
                                target.GameId,
                                target.CanonicalTitle,
                                "howlongtobeat",
                                playtime.MainStoryMinutes,
                                playtime.StoryAndExtrasMinutes,
                                playtime.CompletionistMinutes,
                                DateTimeOffset.UtcNow,
                                token);
                            Interlocked.Increment(ref howLongToBeatCount);
                        }

                        await _repository.MarkPlaytimeLookupCheckedAsync(
                            target.GameId,
                            DateTimeOffset.UtcNow,
                            token);
                    }
                }
            });

        return new CatalogEnrichmentResult(
            targets.Count,
            dueTargets.Length,
            metadataCount,
            estimatedCount,
            howLongToBeatCount);
    }

    public void Dispose() => _client.Dispose();

    private async Task<SteamGameMetadata?> FetchSteamMetadataAsync(
        string appId,
        string canonicalTitle,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&l=spanish");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _steamGate.SendAsync(_client, request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ReportFailure("Steam", $"{uri} respondió HTTP {(int)response.StatusCode}.");
                return null;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty(appId, out var app) ||
                !app.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True ||
                !app.TryGetProperty("data", out var data))
            {
                return null;
            }

            var description = CleanDescription(GetString(data, "short_description"));
            var genres = data.TryGetProperty("genres", out var genreArray) &&
                genreArray.ValueKind == JsonValueKind.Array
                    ? genreArray.EnumerateArray()
                        .Select(genre => GetString(genre, "description"))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>();
            var developer = data.TryGetProperty("developers", out var developers) &&
                developers.ValueKind == JsonValueKind.Array
                    ? developers.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString())
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    : null;
            var releaseDate = data.TryGetProperty("release_date", out var release) &&
                release.ValueKind == JsonValueKind.Object
                    ? NormalizeReleaseDate(GetString(release, "date"))
                    : null;
            var name = GetString(data, "name");
            if (name is not null &&
                TitleMatchingStage.Similarity(canonicalTitle, name) < 0.75)
            {
                ReportFailure("Steam", $"El app ID {appId} no coincide con '{canonicalTitle}'.");
                return null;
            }

            return description is null && genres.Length == 0 &&
                string.IsNullOrWhiteSpace(developer) && releaseDate is null
                    ? null
                    : new SteamGameMetadata(
                        description,
                        genres,
                        developer,
                        releaseDate);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException &&
            !cancellationToken.IsCancellationRequested)
        {
            ReportFailure("Steam", $"{uri}: {exception.Message}");
            return null;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportFailure("Steam", $"{uri} excedió el tiempo de espera: {exception.Message}");
            return null;
        }
    }

    private async Task<SteamGameMetadata?> FetchGogMetadataAsync(
        string title,
        CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"like:{title}");
        var uri = new Uri($"https://catalog.gog.com/v1/catalog?limit=1&query={query}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _gogGate.SendAsync(_client, request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ReportFailure("GOG", $"{uri} respondió HTTP {(int)response.StatusCode}.");
                return null;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("products", out var products) ||
                products.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var best = products.EnumerateArray()
                .Select(product => new
                {
                    Product = product,
                    Name = GetString(product, "title")
                })
                .Where(product => !string.IsNullOrWhiteSpace(product.Name))
                .Select(product => new
                {
                    product.Product,
                    Score = TitleMatchingStage.Similarity(title, product.Name!)
                })
                .OrderByDescending(product => product.Score)
                .FirstOrDefault();
            if (best is null || best.Score < 0.7)
            {
                return null;
            }

            var product = best.Product;
            var genres = product.TryGetProperty("genres", out var genreArray) &&
                genreArray.ValueKind == JsonValueKind.Array
                    ? genreArray.EnumerateArray()
                        .Select(genre => GetString(genre, "name"))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>();
            var developer = product.TryGetProperty("developers", out var developers) &&
                developers.ValueKind == JsonValueKind.Array
                    ? developers.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString())
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    : null;

            return new SteamGameMetadata(
                CleanDescription(
                    GetString(product, "shortDescription") ??
                    GetString(product, "description") ??
                    GetString(product, "summary")),
                genres,
                developer,
                NormalizeReleaseDate(
                    GetString(product, "releaseDate") ??
                    GetString(product, "storeReleaseDate")));
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException &&
            !cancellationToken.IsCancellationRequested)
        {
            ReportFailure("GOG", $"{uri}: {exception.Message}");
            return null;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportFailure("GOG", $"{uri} excedió el tiempo de espera: {exception.Message}");
            return null;
        }
    }

    private static bool HasMissingNarrativeMetadata(SteamGameMetadata? metadata) =>
        metadata is null ||
        string.IsNullOrWhiteSpace(metadata.ShortDescription) ||
        metadata.Genres.Count == 0 ||
        string.IsNullOrWhiteSpace(metadata.Developer) ||
        string.IsNullOrWhiteSpace(metadata.ReleaseDate);

    private static SteamGameMetadata MergeMetadata(
        SteamGameMetadata? steam,
        SteamGameMetadata? gog,
        CatalogSeedGame? seed)
    {
        var genres = steam is { Genres.Count: > 0 }
            ? steam.Genres
            : gog is { Genres.Count: > 0 }
                ? gog.Genres
                : seed?.Genres ?? Array.Empty<string>();
        return new SteamGameMetadata(
            FirstNonEmpty(steam?.ShortDescription, gog?.ShortDescription, seed?.ShortDescription),
            genres,
            FirstNonEmpty(steam?.Developer, gog?.Developer, seed?.Developer),
            FirstNonEmpty(steam?.ReleaseDate, gog?.ReleaseDate, seed?.ReleaseDate));
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private async Task<EstimatedPlaytime?> FetchHowLongToBeatAsync(
        string title,
        CancellationToken cancellationToken)
    {
        var uri = new Uri("https://howlongtobeat.com/api/search");
        var requestBody = new
        {
            searchType = "games",
            searchTerms = title.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            searchPage = 1,
            size = 20,
            searchOptions = new
            {
                games = new
                {
                    userId = 0,
                    platform = "",
                    sortCategory = "popular",
                    rangeTime = new { min = (int?)null, max = (int?)null },
                    rangeYear = new { min = "", max = "" },
                    gameplay = new
                    {
                        perspective = "",
                        flow = "",
                        genre = "",
                        difficulty = ""
                    },
                    rangeCompletion = new { min = (int?)null, max = (int?)null }
                },
                users = new { sortCategory = "postcount" },
                filter = "",
                sort = 0,
                randomizer = 0
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(requestBody),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Referrer = new Uri("https://howlongtobeat.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://howlongtobeat.com");
            using var response = await _howLongToBeatGate.SendAsync(
                _client,
                request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                ReportFailure(
                    "How Long To Beat",
                    $"{uri} respondió HTTP {(int)response.StatusCode} para '{title}'.");
                return null;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var items = GetSearchItems(document.RootElement);
            var best = items
                .Select(item => new
                {
                    Item = item,
                    Name = GetString(item, "game_name") ?? GetString(item, "name")
                })
                .Where(item => item.Name is not null)
                .Select(item => new
                {
                    item.Item,
                    Score = TitleMatchingStage.Similarity(title, item.Name!)
                })
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            if (best is null || best.Score < 0.8)
            {
                return null;
            }

            var main = SecondsToRoundedMinutes(GetNumber(best.Item, "comp_main"));
            var extras = SecondsToRoundedMinutes(GetNumber(best.Item, "comp_plus"));
            var completionist = SecondsToRoundedMinutes(GetNumber(best.Item, "comp_100"));
            return main is null && extras is null && completionist is null
                ? null
                : new EstimatedPlaytime(
                    main ?? extras ?? completionist!.Value,
                    extras ?? main ?? completionist!.Value,
                    completionist ?? extras ?? main!.Value);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException &&
            !cancellationToken.IsCancellationRequested)
        {
            ReportFailure("How Long To Beat", $"{uri} para '{title}': {exception.Message}");
            return null;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportFailure(
                "How Long To Beat",
                $"{uri} para '{title}' excedió el tiempo de espera: {exception.Message}");
            return null;
        }
    }

    private static IEnumerable<JsonElement> GetSearchItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        foreach (var key in new[] { "data", "results" })
        {
            if (root.TryGetProperty(key, out var items) && items.ValueKind == JsonValueKind.Array)
            {
                return items.EnumerateArray().Select(item => item.Clone()).ToArray();
            }
        }

        return Array.Empty<JsonElement>();
    }

    private static decimal? GetNumber(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var number) => number,
            _ => null
        };
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? SecondsToRoundedMinutes(decimal? seconds)
    {
        if (seconds is null or <= 0) return null;
        var tenthsOfAnHour = Math.Round(
            seconds.Value / 360m,
            MidpointRounding.AwayFromZero);
        return checked((int)(tenthsOfAnHour * 6));
    }

    private static string? TryGetSteamAppId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Regex.IsMatch(value, @"^\d+$", RegexOptions.CultureInvariant)) return value;
        var match = SteamAppIdRegex.Match(value);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string? CleanDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var description = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " "));
        description = Regex.Replace(description, @"\s+", " ").Trim();
        return description.Length == 0 ? null : description;
    }

    private static string? NormalizeReleaseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(
                value,
                ["yyyy.MM.dd", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exactDate))
        {
            return exactDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var cultures = new[]
        {
            CultureInfo.GetCultureInfo("es-CR"),
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.InvariantCulture
        };
        foreach (var culture in cultures)
        {
            if (DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var date))
            {
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        return YearRegex.Match(value).Value is { Length: 4 } year ? year : null;
    }

    private static bool HasMissingNarrativeMetadata(CatalogGameMetadata game) =>
        string.IsNullOrWhiteSpace(game.ShortDescription) ||
        game.Genres is not { Count: > 0 } ||
        string.IsNullOrWhiteSpace(game.Developer) ||
        string.IsNullOrWhiteSpace(game.ReleaseDate);

    private static bool HasMissingPlaytime(CatalogGameMetadata game) =>
        game.MainStoryMinutes is null ||
        game.StoryAndExtrasMinutes is null ||
        game.CompletionistMinutes is null ||
        game.MainStoryEstimated ||
        game.StoryAndExtrasEstimated ||
        game.CompletionistEstimated;

    private static bool IsDue(DateTimeOffset? checkedAt, DateTimeOffset now) =>
        checkedAt is null || now - checkedAt.Value >= RefreshInterval;

    private void ReportFailure(string source, string message)
    {
        if (_reportedFailures.TryAdd(source, 0))
        {
            Console.Error.WriteLine($"Aviso de enriquecimiento ({source}): {message}");
        }
    }

    private sealed record SteamGameMetadata(
        string? ShortDescription,
        IReadOnlyList<string> Genres,
        string? Developer,
        string? ReleaseDate)
    {
        public bool HasValues =>
            !string.IsNullOrWhiteSpace(ShortDescription) ||
            Genres.Count > 0 ||
            !string.IsNullOrWhiteSpace(Developer) ||
            !string.IsNullOrWhiteSpace(ReleaseDate);
    }

    private sealed class DomainRequestGate
    {
        private readonly SemaphoreSlim _concurrency;
        private readonly SemaphoreSlim _paceLock = new(1, 1);
        private readonly TimeSpan _minimumInterval;
        private DateTimeOffset _nextRequestAt;

        public DomainRequestGate(int concurrencyLimit, int minimumIntervalMilliseconds)
        {
            ConcurrencyLimit = concurrencyLimit;
            _concurrency = new SemaphoreSlim(concurrencyLimit);
            _minimumInterval = TimeSpan.FromMilliseconds(minimumIntervalMilliseconds);
        }

        public int ConcurrencyLimit { get; }

        public async Task<HttpResponseMessage> SendAsync(
            HttpClient client,
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _concurrency.WaitAsync(cancellationToken);
            try
            {
                await _paceLock.WaitAsync(cancellationToken);
                try
                {
                    var delay = _nextRequestAt - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken);
                    }

                    _nextRequestAt = DateTimeOffset.UtcNow + _minimumInterval;
                }
                finally
                {
                    _paceLock.Release();
                }

                return await client.SendAsync(request, cancellationToken);
            }
            finally
            {
                _concurrency.Release();
            }
        }
    }
}

public sealed record CatalogEnrichmentResult(
    int TotalGames,
    int ProcessedGames,
    int MetadataResults,
    int EstimatedPlaytimeRecords,
    int HowLongToBeatResults = 0);
