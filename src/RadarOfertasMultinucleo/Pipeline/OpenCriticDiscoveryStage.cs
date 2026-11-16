using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml.Linq;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;
using RadarOfertasMultinucleo.Infrastructure.Database;
using RadarOfertasMultinucleo.Infrastructure.Replay;

namespace RadarOfertasMultinucleo.Pipeline;

public sealed class OpenCriticDiscoveryStage : IDiscoveryStage
{
    private const string OpenCriticBase = "https://opencritic.com";
    private const string SteamSearch =
        "https://store.steampowered.com/search/results/?query=&count=100&json=1&start=";

    private static readonly Regex GamePathRegex = new(
        "^/game/(?<id>[0-9]+)/(?<slug>[^/]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TitleRegex = new(
        """<h1\b[^>]*>(?<title>.*?)</h1>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex PlatformBlockRegex = new(
        """<div\b[^>]*class=["'][^"']*\bplatforms\b[^"']*["'][^>]*>(?<block>.*?)</div>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex StrongRegex = new(
        """<strong\b[^>]*>(?<text>.*?)</strong>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ReleaseDateRegex = new(
        """Release Date:</strong>\s*(?<date>[^<]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScoreRegex = new(
        """average score(?: of)?\s*(?<score>[0-9]+(?:\.[0-9]+)?)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OpenGraphImageRegex = new(
        """<meta\s+property=["']og:image["']\s+content=["'](?<url>[^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SteamAppIdRegex = new(
        """/apps/(?<id>[0-9]+)/""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EditionRegex = new(
        """\b(remaster(?:ed)?|remake|definitive|deluxe|goty|game of the year|complete edition|director['’]s cut|ultimate edition|collection)\b""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DlcOrBundleRegex = new(
        """\b(DLC|expansion|season pass|bundle|pack)\b""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RomanNumeralRegex = new(
        """(?<![\p{L}\d])(?:II|III|IV|V|VI|VII|VIII|IX|X)(?![\p{L}\d])""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NumberedSequelRegex = new(
        """(?<!\d)[2-9](?!\d)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PunctuationRegex = new(
        """[^\p{L}\p{N}\s]""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly PipelineOptions _options;
    private readonly SqliteCatalogRepository _repository;
    private readonly ReplayableHttpClient _http;
    private readonly TitleCandidatePool _titleCandidates;
    private readonly CatalogSeedCatalog _seedCatalog;

    public OpenCriticDiscoveryStage(
        PipelineOptions options,
        SqliteCatalogRepository repository,
        ReplayableHttpClient http,
        TitleCandidatePool titleCandidates,
        CatalogSeedCatalog seedCatalog)
    {
        _options = options;
        _repository = repository;
        _http = http;
        _titleCandidates = titleCandidates;
        _seedCatalog = seedCatalog;
    }

    public async Task<IReadOnlyList<GameRecord>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var games = new List<GameRecord>();
        await DiscoverCoreAsync(game =>
        {
            games.Add(game);
            return Task.CompletedTask;
        }, cancellationToken);
        return games;
    }

    public Task RunAsync(
        ChannelWriter<GameRecord> output,
        CancellationToken cancellationToken) =>
        DiscoverCoreAsync(
            game => output.WriteAsync(game, cancellationToken).AsTask(),
            cancellationToken);

    private async Task DiscoverCoreAsync(
        Func<GameRecord, Task> publish,
        CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var existingTitles = await _repository.ReclassifyExistingGamesAsync(cancellationToken);
        var discovered = new List<GameRecord>();
        var visitedIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedTitles = new HashSet<string>(StringComparer.Ordinal);
        var normalizedSeedTitles = existingTitles
            .Select(NormalizeTitle)
            .ToHashSet(StringComparer.Ordinal);
        var pageCount = 0;
        var hardCases = 0;
        var platformFamilies = new HashSet<string>(StringComparer.Ordinal);

        var sitemapIndex = XDocument.Parse(
            await _http.GetStringAsync(new Uri(OpenCriticBase + "/sitemap.xml"), cancellationToken));
        var gameSitemaps = sitemapIndex
            .Descendants()
            .Where(node => node.Name.LocalName == "loc")
            .Select(node => node.Value)
            .Where(location => location.Contains("/sitemap_games_", StringComparison.Ordinal))
            .ToArray();

        var allGameUrls = new List<string>();
        foreach (var sitemapUrl in gameSitemaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sitemap = XDocument.Parse(await _http.GetStringAsync(new Uri(sitemapUrl), cancellationToken));
            allGameUrls.AddRange(sitemap
                .Descendants()
                .Where(node => node.Name.LocalName == "loc")
                .Select(node => node.Value)
                .Where(location =>
                {
                    var uri = new Uri(location);
                    return GamePathRegex.IsMatch(uri.AbsolutePath);
                }));
        }

        _titleCandidates.ReplaceFromSitemapUrls(
            allGameUrls.Distinct(StringComparer.Ordinal),
            Math.Max(_options.CatalogMinGames, _options.CatalogMinGames * 2));

        foreach (var gameUrl in allGameUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageCount >= _options.CatalogMaxPages ||
                (discovered.Count >= _options.CatalogMinGames &&
                 hardCases >= _options.CatalogMinDifficultCases &&
                 platformFamilies.Count >= 3))
            {
                break;
            }

            var gameUri = new Uri(gameUrl);
            var pathMatch = GamePathRegex.Match(gameUri.AbsolutePath);
            var openCriticId = pathMatch.Groups["id"].Value;
            if (!visitedIds.Add(openCriticId))
            {
                continue;
            }

            pageCount++;
            var page = await _http.GetStringAsync(gameUri, cancellationToken);
            var game = ParseOpenCriticGame(openCriticId, gameUrl, page);
            if (game is null)
            {
                continue;
            }

            await SaveAndPublishAsync(game, discovered, publish, cancellationToken);
            normalizedTitles.Add(NormalizeTitle(game.CanonicalTitle));
            hardCases += game.DifficultCaseTags.Count > 0 ? 1 : 0;
            foreach (var platform in game.Platforms)
            {
                platformFamilies.Add(GetPlatformFamily(platform));
            }

            if (pageCount >= _options.CatalogMaxPages ||
                (discovered.Count >= _options.CatalogMinGames &&
                 hardCases >= _options.CatalogMinDifficultCases &&
                 platformFamilies.Count >= 3))
            {
                break;
            }
        }

        await ImportCatalogSeedsAsync(
            discovered,
            normalizedTitles,
            normalizedSeedTitles,
            publish,
            cancellationToken);

        await ImportSteamPagesAsync(
            discovered,
            normalizedTitles,
            Math.Max(0, _options.CatalogMaxPages - pageCount),
            publish,
            cancellationToken);

        hardCases = discovered.Count(game => game.DifficultCaseTags.Count > 0);
        platformFamilies = discovered.SelectMany(game => game.Platforms)
            .Select(GetPlatformFamily)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (discovered.Count < _options.CatalogMinGames ||
            hardCases < _options.CatalogMinDifficultCases ||
            platformFamilies.Count < 3)
        {
            throw new InvalidOperationException(
                $"Catálogo incompleto: {discovered.Count} juegos, {hardCases} casos difíciles, " +
                $"{platformFamilies.Count} familias de plataforma tras {pageCount} páginas OpenCritic. " +
                $"Se requieren {_options.CatalogMinGames}, {_options.CatalogMinDifficultCases} y 3.");
        }
    }

    private async Task ImportCatalogSeedsAsync(
        List<GameRecord> discovered,
        HashSet<string> normalizedTitles,
        HashSet<string> normalizedSeedTitles,
        Func<GameRecord, Task> publish,
        CancellationToken cancellationToken)
    {
        foreach (var seed in _seedCatalog.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedTitle = NormalizeTitle(seed.CanonicalTitle);
            if (!normalizedSeedTitles.Add(normalizedTitle) ||
                !normalizedTitles.Add(normalizedTitle))
            {
                continue;
            }

            var game = new GameRecord(
                StableGuid(seed.ExternalId),
                seed.ExternalId,
                seed.CanonicalTitle,
                Array.Empty<string>(),
                seed.Platforms,
                null,
                null,
                null,
                GameTypeClassifier.Classify(seed.CanonicalTitle, _seedCatalog.KnownDlcTitles),
                seed.SourceUrl,
                seed.CoverImageUrl,
                null,
                new[] { "dlc_conocido" });
            await SaveAndPublishAsync(game, discovered, publish, cancellationToken);
        }
    }

    private async Task ImportSteamPagesAsync(
        List<GameRecord> discovered,
        HashSet<string> normalizedTitles,
        int remainingPageBudget,
        Func<GameRecord, Task> publish,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var pages = Math.Min(5, remainingPageBudget);
        for (var pageNumber = 0; pageNumber < pages; pageNumber++)
        {
            var start = pageNumber * pageSize;
            var body = await _http.GetStringAsync(
                new Uri(SteamSearch + start.ToString(CultureInfo.InvariantCulture)),
                cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var title = WebUtility.HtmlDecode(nameElement.GetString() ?? string.Empty).Trim();
                if (title.Length == 0 || !normalizedTitles.Add(NormalizeTitle(title)))
                {
                    continue;
                }

                var logoUrl = item.TryGetProperty("logo", out var logoElement)
                    ? logoElement.GetString()
                    : null;
                if (logoUrl is null)
                {
                    continue;
                }

                var appIdMatch = SteamAppIdRegex.Match(logoUrl);
                if (!appIdMatch.Success)
                {
                    continue;
                }

                var appId = appIdMatch.Groups["id"].Value;
                var game = new GameRecord(
                    StableGuid("steam:" + appId),
                    "steam:" + appId,
                    title,
                    Array.Empty<string>(),
                    new[] { "PC" },
                    null,
                    null,
                    "USD",
                    GameTypeClassifier.Classify(title, _seedCatalog.KnownDlcTitles),
                    $"https://store.steampowered.com/app/{appId}/",
                    logoUrl,
                    null,
                    ClassifyDifficultCase(title));

                await SaveAndPublishAsync(game, discovered, publish, cancellationToken);
            }
        }
    }

    private async Task SaveAndPublishAsync(
        GameRecord game,
        List<GameRecord> discovered,
        Func<GameRecord, Task> publish,
        CancellationToken cancellationToken)
    {
        await _repository.SaveGameAsync(game, cancellationToken);
        discovered.Add(game);
        await publish(game);
    }

    private GameRecord? ParseOpenCriticGame(string id, string url, string html)
    {
        var titleMatch = TitleRegex.Match(html);
        var platformBlock = PlatformBlockRegex.Match(html);
        if (!titleMatch.Success || !platformBlock.Success)
        {
            return null;
        }

        var title = CleanHtml(titleMatch.Groups["title"].Value);
        if (title.Length == 0)
        {
            return null;
        }

        var platformValues = StrongRegex.Matches(platformBlock.Groups["block"].Value)
            .Cast<Match>()
            .Select(match => CleanHtml(match.Groups["text"].Value))
            .Where(value => value.Length > 0 && !value.Equals("Release Date:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (platformValues.Length == 0)
        {
            return null;
        }

        DateOnly? releaseDate = null;
        var releaseMatch = ReleaseDateRegex.Match(platformBlock.Groups["block"].Value);
        if (releaseMatch.Success &&
            DateTime.TryParse(
                CleanHtml(releaseMatch.Groups["date"].Value).Trim().TrimEnd('-', ' '),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsedDate))
        {
            releaseDate = DateOnly.FromDateTime(parsedDate);
        }

        decimal? rating = null;
        var scoreMatch = ScoreRegex.Match(WebUtility.HtmlDecode(html));
        if (scoreMatch.Success &&
            decimal.TryParse(
                scoreMatch.Groups["score"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsedScore))
        {
            rating = parsedScore;
        }

        var imageMatch = OpenGraphImageRegex.Match(html);
        var image = imageMatch.Success
            ? WebUtility.HtmlDecode(imageMatch.Groups["url"].Value)
            : null;
        var externalId = "opencritic:" + id;

        return new GameRecord(
            StableGuid(externalId),
            externalId,
            title,
            Array.Empty<string>(),
            platformValues,
            releaseDate,
            null,
            null,
            GameTypeClassifier.Classify(title, _seedCatalog.KnownDlcTitles),
            url,
            image,
            rating,
            ClassifyDifficultCase(title));
    }

    private static IReadOnlyList<string> ClassifyDifficultCase(string title)
    {
        var tags = new List<string>();
        if (EditionRegex.IsMatch(title))
        {
            tags.Add("edicion_o_remaster");
        }

        if (DlcOrBundleRegex.IsMatch(title))
        {
            tags.Add("dlc_o_bundle");
        }

        if (RomanNumeralRegex.IsMatch(title))
        {
            tags.Add("numero_romano");
        }

        if (NumberedSequelRegex.IsMatch(title))
        {
            tags.Add("secuela_numerada");
        }

        if (PunctuationRegex.IsMatch(title))
        {
            tags.Add("puntuacion_o_caracter_especial");
        }

        return tags;
    }

    private static string CleanHtml(string value) =>
        WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", string.Empty)).Trim();

    private static string NormalizeTitle(string title) =>
        Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static string GetPlatformFamily(string platform)
    {
        if (platform.Contains("playstation", StringComparison.OrdinalIgnoreCase) ||
            platform.Contains("ps vita", StringComparison.OrdinalIgnoreCase))
        {
            return "PlayStation";
        }

        if (platform.Contains("xbox", StringComparison.OrdinalIgnoreCase))
        {
            return "Xbox";
        }

        if (platform.Contains("nintendo", StringComparison.OrdinalIgnoreCase))
        {
            return "Nintendo";
        }

        if (platform.Equals("PC", StringComparison.OrdinalIgnoreCase))
        {
            return "PC";
        }

        return platform;
    }

    internal static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
