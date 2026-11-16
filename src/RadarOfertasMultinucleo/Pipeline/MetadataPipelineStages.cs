using System.Globalization;
using System.Net;
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
    private readonly double _threshold;

    public TitleMatchingStage(double threshold)
    {
        _threshold = threshold;
    }

    public Task<MatchedOffer?> MatchAsync(
        ParsedCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var left = Normalize(candidate.CanonicalTitle);
        var right = Normalize(candidate.CandidateTitle);
        var similarity = Similarity(left, right);
        if (similarity < _threshold)
        {
            return Task.FromResult<MatchedOffer?>(null);
        }

        return Task.FromResult<MatchedOffer?>(new MatchedOffer(
            candidate.GameId,
            candidate,
            similarity,
            similarity - _threshold < 0.05,
            false,
            null));
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return left == right ? 1 : 0;
        }

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
                candidate.SourceId,
                rating,
                candidate.RetrievedAt,
                cancellationToken)
            : 0;

        return new PersistedOffer(
            observationId,
            offer.GameId,
            candidate.SourceId,
            candidate.CanonicalTitle,
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
            candidate.RetrievedAt);
    }
}

public sealed class JsonPublishStage : IPublishStage
{
    private readonly string _outputPath;
    private readonly List<PersistedOffer> _offers = new();
    private readonly SemaphoreSlim _sync = new(1, 1);

    public JsonPublishStage()
    {
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
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                _offers
                    .OrderBy(offer => offer.GameId)
                    .ThenBy(offer => offer.SourceId, StringComparer.Ordinal),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                },
                cancellationToken);
        }

        File.Move(temporaryPath, _outputPath, overwrite: true);
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
