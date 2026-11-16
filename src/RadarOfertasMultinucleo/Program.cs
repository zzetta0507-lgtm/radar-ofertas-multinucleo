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

            using var http = new ReplayableHttpClient(
                options,
                command.SourceMode,
                command.RecordReplay);
            var repository = new SqliteCatalogRepository(options);
            var discovery = new OpenCriticDiscoveryStage(options, repository, http);
            var runner = new PipelineRunner(
                options,
                discovery,
                new OpenCriticDownloadStage(http),
                new OpenCriticParseStage(),
                new TitleMatchingStage(options.MatchingThreshold),
                new SqlitePersistStage(repository),
                new JsonPublishStage());

            Console.WriteLine($"Iniciando pipeline en modo {options.Mode}; fuente {command.SourceMode}.");
            await runner.RunAsync(options.Mode, CancellationToken.None);
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

    private static RunCommand ParseArguments(PipelineOptions options, string[] args)
    {
        var sourceMode = HttpSourceMode.Live;
        var recordReplay = false;
        var benchmarkContention = false;
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
                    if (++index >= args.Length || !args[index].Equals("replay", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("El único origen sin red disponible es --source replay.");
                    }

                    sourceMode = HttpSourceMode.Replay;
                    break;
                case "--record-replay":
                    recordReplay = true;
                    break;
                case "--benchmark-contention":
                    benchmarkContention = true;
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

        return new RunCommand(sourceMode, recordReplay, benchmarkContention);
    }

    private sealed record RunCommand(
        HttpSourceMode SourceMode,
        bool RecordReplay,
        bool BenchmarkContention);
}
