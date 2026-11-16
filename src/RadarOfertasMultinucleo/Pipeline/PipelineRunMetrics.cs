using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Pipeline;

public sealed class PipelineRunMetrics
{
    public const string DiscoveryStage = "Descubrimiento";
    public const string DownloadStage = "Descarga";
    public const string ParseStage = "Análisis";
    public const string MatchStage = "Emparejamiento";
    public const string PersistStage = "Persistencia";
    public const string PublishStage = "Publicación";

    private static readonly string[] StageNames =
    {
        DiscoveryStage,
        DownloadStage,
        ParseStage,
        MatchStage,
        PersistStage,
        PublishStage
    };

    private readonly Stopwatch _totalDuration = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, double> _stageDurations = new();
    private readonly ConcurrentDictionary<string, RequestCounter> _requestsBySite =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, byte> _matchedCandidates = new();
    private readonly ConcurrentDictionary<Guid, byte> _unmatchedCandidates = new();
    private readonly ConcurrentDictionary<Guid, byte> _uncertainMatches = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public async Task<T> MeasureStageAsync<T>(string stage, Func<Task<T>> operation)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            return await operation();
        }
        finally
        {
            var elapsed = timer.Elapsed.TotalMilliseconds;
            _stageDurations.AddOrUpdate(
                stage,
                elapsed,
                (_, total) => total + elapsed);
        }
    }

    public async Task MeasureStageAsync(string stage, Func<Task> operation)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await operation();
        }
        finally
        {
            var elapsed = timer.Elapsed.TotalMilliseconds;
            _stageDurations.AddOrUpdate(
                stage,
                elapsed,
                (_, total) => total + elapsed);
        }
    }

    public void RecordMatch(Guid candidateGameId, MatchedOffer? match)
    {
        if (match is null)
        {
            _unmatchedCandidates.TryAdd(candidateGameId, 0);
            return;
        }

        _matchedCandidates.TryAdd(candidateGameId, 0);
        if (match.IsUncertain)
        {
            _uncertainMatches.TryAdd(candidateGameId, 0);
        }
    }

    public void RecordHttpRequest(Uri uri, bool successful)
    {
        var site = SiteName(uri.Host);
        _requestsBySite.GetOrAdd(site, _ => new RequestCounter()).Record(successful);
    }

    public PipelineMetricsReport CreateReport(ExecutionMode mode)
    {
        var now = DateTimeOffset.UtcNow;
        return new PipelineMetricsReport(
            now,
            _startedAt,
            mode.ToString(),
            _totalDuration.Elapsed.TotalMilliseconds,
            _unmatchedCandidates.Keys.Except(_matchedCandidates.Keys).Count(),
            _uncertainMatches.Count,
            StageNames.Select(name => new StageDurationMetric(
                name,
                _stageDurations.TryGetValue(name, out var duration) ? duration : 0)).ToArray(),
            _requestsBySite
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Value.ToMetric(entry.Key))
                .ToArray());
    }

    public static async Task WriteAsync(
        PipelineMetricsReport report,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine("web", "data", "metrics.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                },
                cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string SiteName(string host)
    {
        if (host.Contains("opencritic", StringComparison.OrdinalIgnoreCase)) return "OpenCritic";
        if (host.Contains("steampowered", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (host.Contains("gamersgate", StringComparison.OrdinalIgnoreCase)) return "GamersGate";
        if (host.Contains("cheapshark", StringComparison.OrdinalIgnoreCase)) return "CheapShark";
        if (host.Equals("gog.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".gog.com", StringComparison.OrdinalIgnoreCase)) return "GOG";
        if (host.Contains("howlongtobeat", StringComparison.OrdinalIgnoreCase)) return "HLTB";
        return host;
    }

    private sealed class RequestCounter
    {
        private int _successful;
        private int _failed;

        public void Record(bool successful)
        {
            if (successful)
            {
                Interlocked.Increment(ref _successful);
            }
            else
            {
                Interlocked.Increment(ref _failed);
            }
        }

        public SiteRequestMetric ToMetric(string site)
        {
            var successful = Volatile.Read(ref _successful);
            var failed = Volatile.Read(ref _failed);
            return new SiteRequestMetric(site, successful, 0, failed, successful + failed);
        }
    }
}

public sealed record PipelineMetricsReport(
    DateTimeOffset GeneratedAt,
    DateTimeOffset LastRunAt,
    string Mode,
    double DurationMs,
    int UnmatchedGames,
    int UncertainMatches,
    IReadOnlyList<StageDurationMetric> StageDurations,
    IReadOnlyList<SiteRequestMetric> RequestsBySite);

public sealed record StageDurationMetric(string Name, double DurationMs);

public sealed record SiteRequestMetric(
    string Site,
    int Successful,
    int Retried,
    int Failed,
    int Total);
