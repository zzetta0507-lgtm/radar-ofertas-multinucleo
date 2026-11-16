using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;
using RadarOfertasMultinucleo.Infrastructure.Database;
using RadarOfertasMultinucleo.Infrastructure.Replay;

namespace RadarOfertasMultinucleo.Pipeline;

public sealed class OpenCriticDownloadStage : IDownloadStage
{
    private readonly ReplayableHttpClient _http;

    public OpenCriticDownloadStage(ReplayableHttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<SourceResponse>> DownloadAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        if (!game.ExternalId.StartsWith("opencritic:", StringComparison.Ordinal))
        {
            return Array.Empty<SourceResponse>();
        }

        var uri = new Uri(game.SourceUrl);
        var page = await _http.GetPageAsync(uri, cancellationToken);
        return new[]
        {
            new SourceResponse(
                game.Id,
                "opencritic",
                game.CanonicalTitle,
                game.Platforms,
                game.SourceUrl,
                page.ContentType,
                page.Body,
                page.RetrievedAt,
                true,
                null)
        };
    }
}

public sealed class OpenCriticParseStage : IParseStage
{
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

        var titleMatch = TitleRegex.Match(response.Body);
        if (!titleMatch.Success)
        {
            throw new InvalidDataException(
                $"La respuesta de OpenCritic para {response.CanonicalTitle} no contiene título.");
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
            response.RetrievedAt));
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

    private readonly double _threshold;
    private readonly TitleCandidatePool _titleCandidates;
    private int _maxDegreeOfParallelism = 1;

    public TitleMatchingStage(double threshold, TitleCandidatePool titleCandidates)
    {
        _threshold = threshold;
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
            false,
            null));
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
        left = Normalize(left);
        right = Normalize(right);
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
        var observationId = candidate.Rating is { } rating
            ? await _repository.SaveRatingAsync(
                offer.GameId,
                offer.MatchedCanonicalTitle,
                candidate.SourceId,
                rating,
                candidate.RetrievedAt,
                cancellationToken)
            : 0;

        return new PersistedOffer(
            observationId,
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
            offer.IsUncertain);
    }
}

public sealed class JsonPublishStage : IPublishStage
{
    private readonly string _outputPath;
    private readonly SqliteCatalogRepository _repository;
    private readonly List<PersistedOffer> _offers = new();
    private readonly SemaphoreSlim _sync = new(1, 1);

    public JsonPublishStage(SqliteCatalogRepository repository)
    {
        _repository = repository;
        _outputPath = Path.GetFullPath(Path.Combine("web", "data", "catalog.json"));
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
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        var temporaryPath = _outputPath + ".tmp";
        var metadata = await _repository.ReadCatalogMetadataAsync(cancellationToken);
        var offersByGame = _offers
            .GroupBy(offer => offer.GameId)
            .ToDictionary(
                group => group.Key.ToString("D"),
                group => (IReadOnlyList<PersistedOffer>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
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
                    offers);
            });

        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                catalog,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                },
                cancellationToken);
        }

        File.Move(temporaryPath, _outputPath, overwrite: true);
    }

    private static string NormalizeTitle(string title) =>
        Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

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
        IReadOnlyList<PersistedOffer> Offers);
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
        CancellationToken cancellationToken)
    {
        await foreach (var candidate in input.ReadAllAsync(cancellationToken))
        {
            var matched = await stage.MatchAsync(candidate, cancellationToken);
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
