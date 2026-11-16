using System.Text.Json.Serialization;

namespace RadarOfertasMultinucleo.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionMode
{
    Sequential,
    Concurrent,
    Parallel
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DealRule
{
    RepositoryMsrp,
    StoreRegularPrice,
    Combined
}

public sealed class PipelineOptions
{
    [JsonPropertyName("mode")]
    public ExecutionMode Mode { get; set; } = ExecutionMode.Sequential;

    [JsonPropertyName("workers_io")]
    public int WorkersIo { get; init; }

    [JsonPropertyName("workers_cpu")]
    public int WorkersCpu { get; init; }

    [JsonPropertyName("queue_size_stage1")]
    public int QueueSizeStage1 { get; init; }

    [JsonPropertyName("queue_size_stage2")]
    public int QueueSizeStage2 { get; init; }

    [JsonPropertyName("queue_size_stage3")]
    public int QueueSizeStage3 { get; init; }

    [JsonPropertyName("queue_size_stage4")]
    public int QueueSizeStage4 { get; init; }

    [JsonPropertyName("queue_size_stage5")]
    public int QueueSizeStage5 { get; init; }

    [JsonPropertyName("max_concurrent_requests_per_domain")]
    public int MaxConcurrentRequestsPerDomain { get; init; }

    [JsonPropertyName("database_path")]
    public string DatabasePath { get; init; } = string.Empty;

    [JsonPropertyName("matching_threshold")]
    public double MatchingThreshold { get; init; }

    [JsonPropertyName("deal_rule")]
    public DealRule DealRule { get; init; } = DealRule.Combined;

    [JsonPropertyName("catalog_min_games")]
    public int CatalogMinGames { get; init; }

    [JsonPropertyName("catalog_min_difficult_cases")]
    public int CatalogMinDifficultCases { get; init; }

    [JsonPropertyName("catalog_max_pages")]
    public int CatalogMaxPages { get; init; }

    [JsonPropertyName("catalog_seed_file")]
    public string CatalogSeedFile { get; init; } = string.Empty;

    [JsonPropertyName("domain_min_interval_ms")]
    public int DomainMinIntervalMilliseconds { get; init; }

    [JsonPropertyName("replay_directory")]
    public string ReplayDirectory { get; init; } = string.Empty;

    [JsonPropertyName("replay_latency_fixed_ms")]
    public int ReplayLatencyFixedMilliseconds { get; init; }

    [JsonPropertyName("replay_latency_jitter_ms")]
    public int ReplayLatencyJitterMilliseconds { get; init; }

    [JsonPropertyName("replay_latency_seed")]
    public int ReplayLatencySeed { get; init; }

    [JsonPropertyName("contention_operations")]
    public int ContentionOperations { get; init; }

    public PipelineOptions ForExecution(
        ExecutionMode mode,
        int workersIo,
        int workersCpu,
        string? databasePath = null) =>
        new()
        {
            Mode = mode,
            WorkersIo = workersIo,
            WorkersCpu = workersCpu,
            QueueSizeStage1 = QueueSizeStage1,
            QueueSizeStage2 = QueueSizeStage2,
            QueueSizeStage3 = QueueSizeStage3,
            QueueSizeStage4 = QueueSizeStage4,
            QueueSizeStage5 = QueueSizeStage5,
            MaxConcurrentRequestsPerDomain = MaxConcurrentRequestsPerDomain,
            DatabasePath = databasePath ?? DatabasePath,
            MatchingThreshold = MatchingThreshold,
            DealRule = DealRule,
            CatalogMinGames = CatalogMinGames,
            CatalogMinDifficultCases = CatalogMinDifficultCases,
            CatalogMaxPages = CatalogMaxPages,
            CatalogSeedFile = CatalogSeedFile,
            DomainMinIntervalMilliseconds = DomainMinIntervalMilliseconds,
            ReplayDirectory = ReplayDirectory,
            ReplayLatencyFixedMilliseconds = ReplayLatencyFixedMilliseconds,
            ReplayLatencyJitterMilliseconds = ReplayLatencyJitterMilliseconds,
            ReplayLatencySeed = ReplayLatencySeed,
            ContentionOperations = ContentionOperations
        };

    public void Validate()
    {
        if (WorkersIo < 1 || WorkersCpu < 1)
        {
            throw new ArgumentException("workers_io y workers_cpu deben ser mayores que cero.");
        }

        if (QueueSizeStage1 < 1 || QueueSizeStage2 < 1 ||
            QueueSizeStage3 < 1 || QueueSizeStage4 < 1 || QueueSizeStage5 < 1)
        {
            throw new ArgumentException("Todos los tamaños de cola deben ser mayores que cero.");
        }

        if (MaxConcurrentRequestsPerDomain < 1)
        {
            throw new ArgumentException("max_concurrent_requests_per_domain debe ser mayor que cero.");
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new ArgumentException("database_path no puede estar vacío.");
        }

        if (MatchingThreshold is < 0 or > 1)
        {
            throw new ArgumentException("matching_threshold debe estar entre 0 y 1.");
        }

        if (CatalogMinGames < 1 ||
            CatalogMinDifficultCases < 0 ||
            CatalogMinDifficultCases > CatalogMinGames ||
            CatalogMaxPages < CatalogMinGames ||
            string.IsNullOrWhiteSpace(CatalogSeedFile))
        {
            throw new ArgumentException("Los límites de descubrimiento no son válidos.");
        }

        if (DomainMinIntervalMilliseconds < 0 ||
            ReplayLatencyFixedMilliseconds < 0 ||
            ReplayLatencyJitterMilliseconds < 0 ||
            ReplayLatencyFixedMilliseconds > 600_000 ||
            ReplayLatencyJitterMilliseconds > 600_000)
        {
            throw new ArgumentException("Los retardos deben ser no negativos y no superar diez minutos.");
        }

        if (string.IsNullOrWhiteSpace(ReplayDirectory) || ContentionOperations < 1)
        {
            throw new ArgumentException("replay_directory y contention_operations deben ser válidos.");
        }
    }
}
