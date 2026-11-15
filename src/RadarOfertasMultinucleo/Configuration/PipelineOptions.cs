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
    }
}
