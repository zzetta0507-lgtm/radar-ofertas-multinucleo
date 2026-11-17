using System.Text.Json;
using System.Text.Json.Serialization;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Experiments;
using RadarOfertasMultinucleo.Infrastructure.Database;
using RadarOfertasMultinucleo.Infrastructure.Replay;
using RadarOfertasMultinucleo.Pipeline;

return await App.RunAsync(args);

internal static class App
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            await using var configFile = File.OpenRead(configPath);
            var options = await JsonSerializer.DeserializeAsync<PipelineOptions>(
                configFile,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                }) ?? throw new InvalidDataException("config.json está vacío o no es válido.");

            var command = ParseArguments(options, args);
            options.Validate();

            if (command.BenchmarkContention)
            {
                await new ContentionBenchmark(options).RunAsync(CancellationToken.None);
                Console.WriteLine("Benchmark de contención guardado en data/benchmark-contention.json.");
                return 0;
            }

            if (command.SeedHistory)
            {
                var seedCatalog = await CatalogSeedCatalog.LoadAsync(
                    options.CatalogSeedFile,
                    CancellationToken.None);
                var repository = new SqliteCatalogRepository(options, seedCatalog);
                await repository.InitializeAsync(CancellationToken.None);
                var result = await new PriceHistorySeedService(repository)
                    .SeedThroughTodayAsync(CancellationToken.None);
                await new JsonPublishStage(repository).CompleteAsync(CancellationToken.None);
                Console.WriteLine(
                    $"Historial hasta {result.ThroughDate:yyyy-MM-dd}: " +
                    $"{result.GameStorePairs} pares juego-tienda, " +
                    $"{result.EstimatedObservations} puntos estimados añadidos, " +
                    $"{result.ImportedObservations} observaciones existentes importadas y " +
                    $"{result.PairsWithRealCurrentPrice} precios reales del día.");
                return 0;
            }

            var validation = TitleMatchingStage.Validate(
                MatchingValidationSet.Pairs,
                options.MatchingThreshold);
            Console.WriteLine(
                $"Validación de emparejamiento: {validation.PairCount} pares, " +
                $"precisión {validation.Precision:P1}, exhaustividad {validation.Recall:P1}.");
            await WriteMatchingValidationAsync(validation, CancellationToken.None);
            if (command.ValidateMatching)
            {
                if (validation.FalsePositives > 0 || validation.FalseNegatives > 0)
                {
                    Console.Error.WriteLine(
                        $"La validación no pasó: {validation.FalsePositives} falsos positivos y " +
                        $"{validation.FalseNegatives} falsos negativos.");
                    return 1;
                }

                return 0;
            }

            if (command.RunBenchmarks)
            {
                var benchmark = new PipelineBenchmarkRunner(
                    options,
                    (runOptions, cancellationToken) =>
                        RunPipelineAsync(runOptions, HttpSourceMode.Replay, false, cancellationToken));
                await benchmark.RunAsync(CancellationToken.None);
                Console.WriteLine("Benchmarks exportados a web/data/benchmarks.json y benchmarks.csv.");
                return 0;
            }

            Console.WriteLine(
                $"Iniciando pipeline en modo {options.Mode}; fuente {command.SourceMode}.");
            await RunPipelineAsync(
                options,
                command.SourceMode,
                command.RecordReplay,
                CancellationToken.None,
                exportMetrics: true);
            Console.WriteLine("Pipeline finalizado; catálogo actualizado en data/radar.db.");
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or ArgumentException or
                HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"No se pudo iniciar el radar: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunPipelineAsync(
        PipelineOptions options,
        HttpSourceMode sourceMode,
        bool recordReplay,
        CancellationToken cancellationToken,
        bool exportMetrics = false)
    {
        var metrics = new PipelineRunMetrics();
        var seedCatalog = await CatalogSeedCatalog.LoadAsync(
            options.CatalogSeedFile,
            cancellationToken);
        using var http = new ReplayableHttpClient(options, sourceMode, recordReplay, metrics);
        var repository = new SqliteCatalogRepository(options, seedCatalog);
        var titleCandidates = new TitleCandidatePool();
        var discovery = new OpenCriticDiscoveryStage(
            options,
            repository,
            http,
            titleCandidates,
            seedCatalog);
        var runner = new PipelineRunner(
            options,
            discovery,
            new StoreAndMetadataDownloadStage(
                http,
                options.MatchingThreshold,
                options.StoreSources,
                metrics),
            new CatalogSourceParseStage(),
            new TitleMatchingStage(options.MatchingThreshold, options.DealRule, titleCandidates),
            new SqlitePersistStage(repository),
            new JsonPublishStage(repository),
            metrics);
        await runner.RunAsync(options.Mode, cancellationToken);
        if (exportMetrics)
        {
            await PipelineRunMetrics.WriteAsync(
                metrics.CreateReport(options.Mode),
                cancellationToken);
        }
    }

    private static async Task WriteMatchingValidationAsync(
        MatchingValidationReport report,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine("web", "data", "matching-validation.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
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

    private static RunCommand ParseArguments(PipelineOptions options, string[] args)
    {
        var sourceMode = HttpSourceMode.Live;
        var recordReplay = false;
        var benchmarkContention = false;
        var runBenchmarks = false;
        var validateMatching = false;
        var seedHistory = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--mode":
                    if (++index >= args.Length ||
                        !Enum.TryParse<ExecutionMode>(args[index], ignoreCase: true, out var mode))
                    {
                        throw new ArgumentException(
                            "Uso: --mode sequential|concurrent|parallel.");
                    }

                    options.Mode = mode;
                    break;
                case "--source":
                    if (++index >= args.Length ||
                        (!args[index].Equals("replay", StringComparison.OrdinalIgnoreCase) &&
                         !args[index].Equals("live", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new ArgumentException("Uso: --source live|replay.");
                    }

                    sourceMode = args[index].Equals("replay", StringComparison.OrdinalIgnoreCase)
                        ? HttpSourceMode.Replay
                        : HttpSourceMode.Live;
                    break;
                case "--record-replay":
                    recordReplay = true;
                    break;
                case "--benchmark-contention":
                    benchmarkContention = true;
                    break;
                case "--run-benchmarks":
                    runBenchmarks = true;
                    break;
                case "--validate-matching":
                    validateMatching = true;
                    break;
                case "--seed-history":
                    seedHistory = true;
                    break;
                default:
                    throw new ArgumentException($"Argumento desconocido: {args[index]}.");
            }
        }

        if (recordReplay && sourceMode == HttpSourceMode.Replay)
        {
            throw new ArgumentException("--record-replay no se combina con --source replay.");
        }

        if (benchmarkContention && (recordReplay || sourceMode == HttpSourceMode.Replay))
        {
            throw new ArgumentException(
                "--benchmark-contention es aislado y no acepta --record-replay ni --source replay.");
        }

        if (runBenchmarks && (sourceMode != HttpSourceMode.Replay || recordReplay || benchmarkContention))
        {
            throw new ArgumentException(
                "--run-benchmarks requiere --source replay y no se combina con grabación ni contención.");
        }

        if (validateMatching &&
            (recordReplay || sourceMode == HttpSourceMode.Replay || benchmarkContention || runBenchmarks))
        {
            throw new ArgumentException("--validate-matching no se combina con otros modos de ejecución.");
        }

        if (seedHistory &&
            (recordReplay || sourceMode == HttpSourceMode.Replay || benchmarkContention ||
             runBenchmarks || validateMatching))
        {
            throw new ArgumentException("--seed-history no se combina con otros modos de ejecución.");
        }

        return new RunCommand(
            sourceMode,
            recordReplay,
            benchmarkContention,
            runBenchmarks,
            validateMatching,
            seedHistory);
    }

    private sealed record RunCommand(
        HttpSourceMode SourceMode,
        bool RecordReplay,
        bool BenchmarkContention,
        bool RunBenchmarks,
        bool ValidateMatching,
        bool SeedHistory);
}
