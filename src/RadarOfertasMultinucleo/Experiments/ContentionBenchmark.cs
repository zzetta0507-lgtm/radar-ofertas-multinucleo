using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RadarOfertasMultinucleo.Configuration;

namespace RadarOfertasMultinucleo.Experiments;

public sealed class ContentionBenchmark
{
    private static readonly string[] Platforms = ["PC", "PlayStation", "Nintendo Switch", "Xbox"];
    private readonly PipelineOptions _options;

    public ContentionBenchmark(PipelineOptions options)
    {
        _options = options;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var raceWorkers = Math.Max(2, _options.WorkersCpu);
        var raceResult = DemonstrateLostUpdate(raceWorkers, cancellationToken);
        var results = new[]
        {
            Measure("sin_sincronizacion", RunUnsynchronized, cancellationToken),
            Measure("lock_global", RunWithGlobalLock, cancellationToken),
            Measure("thread_local", RunWithThreadLocal, cancellationToken),
            Measure("concurrent_dictionary", RunWithConcurrentDictionary, cancellationToken)
        };

        var report = new ContentionReport(
            DateTimeOffset.UtcNow,
            _options.ContentionOperations,
            _options.WorkersCpu,
            raceResult,
            results);
        var outputPath = Path.GetFullPath(Path.Combine("data", "benchmark-contention.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using var stream = File.Create(outputPath);
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

    private RaceDemonstration DemonstrateLostUpdate(
        int workers,
        CancellationToken cancellationToken)
    {
        var sharedPcOffers = 0;
        using var barrier = new Barrier(workers);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = workers,
            CancellationToken = cancellationToken
        };

        Parallel.For(0, workers, parallelOptions, _ =>
        {
            var observedValue = sharedPcOffers;
            barrier.SignalAndWait(cancellationToken);
            sharedPcOffers = observedValue + 1;
        });

        return new RaceDemonstration(
            "PC",
            workers,
            sharedPcOffers,
            workers,
            sharedPcOffers != workers);
    }

    private BenchmarkResult Measure(
        string variant,
        Action<long[]> aggregation,
        CancellationToken cancellationToken)
    {
        var counts = new long[Platforms.Length];
        var stopwatch = Stopwatch.StartNew();
        aggregation(counts);
        stopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        var totals = Platforms
            .Select((platform, index) => new PlatformCount(platform, counts[index]))
            .ToArray();
        return new BenchmarkResult(
            variant,
            stopwatch.Elapsed.TotalMilliseconds,
            totals,
            totals.Sum(item => item.Offers));
    }

    private void RunUnsynchronized(long[] counts)
    {
        Parallel.For(
            0,
            _options.ContentionOperations,
            new ParallelOptions { MaxDegreeOfParallelism = _options.WorkersCpu },
            index => counts[index % counts.Length]++);
    }

    private void RunWithGlobalLock(long[] counts)
    {
        var sync = new object();
        Parallel.For(
            0,
            _options.ContentionOperations,
            new ParallelOptions { MaxDegreeOfParallelism = _options.WorkersCpu },
            index =>
            {
                lock (sync)
                {
                    counts[index % counts.Length]++;
                }
            });
    }

    private void RunWithThreadLocal(long[] counts)
    {
        using var localCounters = new ThreadLocal<long[]>(
            () => new long[Platforms.Length],
            trackAllValues: true);
        Parallel.For(
            0,
            _options.ContentionOperations,
            new ParallelOptions { MaxDegreeOfParallelism = _options.WorkersCpu },
            index =>
            {
                var local = localCounters.Value
                    ?? throw new InvalidOperationException("No se inicializó el acumulador local del hilo.");
                local[index % counts.Length]++;
            });

        foreach (var localCounter in localCounters.Values)
        {
            for (var index = 0; index < counts.Length; index++)
            {
                counts[index] += localCounter[index];
            }
        }
    }

    private void RunWithConcurrentDictionary(long[] counts)
    {
        var concurrentCounts = new ConcurrentDictionary<string, long>(
            Platforms.Select(platform => new KeyValuePair<string, long>(platform, 0)),
            StringComparer.Ordinal);
        Parallel.For(
            0,
            _options.ContentionOperations,
            new ParallelOptions { MaxDegreeOfParallelism = _options.WorkersCpu },
            index => concurrentCounts.AddOrUpdate(
                Platforms[index % Platforms.Length],
                1,
                static (_, current) => current + 1));

        for (var index = 0; index < Platforms.Length; index++)
        {
            counts[index] = concurrentCounts[Platforms[index]];
        }
    }

    private sealed record ContentionReport(
        DateTimeOffset CapturedAt,
        int Operations,
        int Workers,
        RaceDemonstration RaceDemonstration,
        IReadOnlyList<BenchmarkResult> Results);

    private sealed record RaceDemonstration(
        string Platform,
        int ExpectedOffers,
        int ObservedOffers,
        int ParticipatingWorkers,
        bool LostUpdateObserved);

    private sealed record BenchmarkResult(
        string Variant,
        double ElapsedMilliseconds,
        IReadOnlyList<PlatformCount> ByPlatform,
        long TotalOffers);

    private sealed record PlatformCount(string Platform, long Offers);
}
