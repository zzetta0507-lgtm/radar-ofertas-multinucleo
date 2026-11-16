using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RadarOfertasMultinucleo.Configuration;

namespace RadarOfertasMultinucleo.Experiments;

public sealed class PipelineBenchmarkRunner
{
    private const int Repetitions = 5;
    private static readonly int[] IoWorkerSweep = [1, 2, 4, 8, 16, 32, 64];
    private readonly PipelineOptions _baseOptions;
    private readonly Func<PipelineOptions, CancellationToken, Task> _runPipeline;

    public PipelineBenchmarkRunner(
        PipelineOptions baseOptions,
        Func<PipelineOptions, CancellationToken, Task> runPipeline)
    {
        _baseOptions = baseOptions;
        _runPipeline = runPipeline;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Benchmark: midiendo línea base secuencial (5 repeticiones).");
        var sequentialMeasurements = await MeasureAsync(
            _baseOptions.ForExecution(ExecutionMode.Sequential, 1, 1),
            cancellationToken);
        var sequentialMedian = Median(sequentialMeasurements);

        var ioMeasurements = new Dictionary<int, double[]>();
        foreach (var workers in IoWorkerSweep)
        {
            Console.WriteLine($"Benchmark E/S: workers_io={workers}.");
            ioMeasurements[workers] = await MeasureAsync(
                _baseOptions.ForExecution(ExecutionMode.Concurrent, workers, 1),
                cancellationToken);
        }

        var logicalProcessors = Environment.ProcessorCount;
        var cpuWorkerSweep = BuildCpuWorkerSweep(logicalProcessors);
        var cpuMeasurements = new Dictionary<int, double[]>();
        foreach (var workers in cpuWorkerSweep)
        {
            Console.WriteLine($"Benchmark CPU: workers_cpu={workers}.");
            cpuMeasurements[workers] = await MeasureAsync(
                _baseOptions.ForExecution(ExecutionMode.Parallel, _baseOptions.WorkersIo, workers),
                cancellationToken);
        }

        var ioBaseline = Median(ioMeasurements[1]);
        var cpuBaseline = Median(cpuMeasurements[1]);
        var report = new PipelineBenchmarkReport(
            DateTimeOffset.UtcNow,
            Repetitions,
            logicalProcessors,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            sequentialMeasurements.Min(),
            sequentialMeasurements.Max(),
            sequentialMedian,
            CreateSummaries(ioMeasurements, sequentialMedian, ioBaseline),
            CreateSummaries(cpuMeasurements, sequentialMedian, cpuBaseline));

        var outputDirectory = Path.GetFullPath(Path.Combine("web", "data"));
        Directory.CreateDirectory(outputDirectory);
        await WriteJsonAsync(Path.Combine(outputDirectory, "benchmarks.json"), report, cancellationToken);
        await WriteCsvAsync(Path.Combine(outputDirectory, "benchmarks.csv"), report, cancellationToken);
    }

    private async Task<double[]> MeasureAsync(
        PipelineOptions options,
        CancellationToken cancellationToken)
    {
        var measurements = new double[Repetitions];
        for (var repetition = 0; repetition < Repetitions; repetition++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqliteConnection.ClearAllPools();
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "radar-ofertas-benchmark-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var isolatedOptions = options.ForExecution(
                    options.Mode,
                    options.WorkersIo,
                    options.WorkersCpu,
                    Path.Combine(temporaryDirectory, "radar.db"));
                var stopwatch = Stopwatch.StartNew();
                await _runPipeline(isolatedOptions, cancellationToken);
                stopwatch.Stop();
                measurements[repetition] = stopwatch.Elapsed.TotalMilliseconds;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        }

        return measurements;
    }

    private static IReadOnlyList<BenchmarkConfigurationSummary> CreateSummaries(
        IReadOnlyDictionary<int, double[]> measurements,
        double sequentialMedian,
        double relativeBaseline)
    {
        return measurements
            .OrderBy(item => item.Key)
            .Select(item =>
            {
                var median = Median(item.Value);
                var relativeSpeedup = relativeBaseline / median;
                var workers = item.Key;
                var karpFlatt = workers == 1
                    ? (double?)null
                    : ((1d / relativeSpeedup) - (1d / workers)) / (1d - (1d / workers));
                return new BenchmarkConfigurationSummary(
                    workers,
                    item.Value.Min(),
                    item.Value.Max(),
                    median,
                    sequentialMedian / median,
                    relativeSpeedup,
                    relativeSpeedup / workers,
                    karpFlatt);
            })
            .ToArray();
    }

    private static int[] BuildCpuWorkerSweep(int logicalProcessors)
    {
        var workers = new SortedSet<int> { 1 };
        for (var count = 2; count < logicalProcessors; count *= 2)
        {
            workers.Add(count);
        }

        workers.Add(logicalProcessors);
        workers.Add(checked(logicalProcessors * 2));
        return workers.ToArray();
    }

    private static double Median(IReadOnlyCollection<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static async Task WriteJsonAsync(
        string path,
        PipelineBenchmarkReport report,
        CancellationToken cancellationToken)
    {
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

    private static async Task WriteCsvAsync(
        string path,
        PipelineBenchmarkReport report,
        CancellationToken cancellationToken)
    {
        var csv = new StringBuilder(
            "sistema_operativo,arquitectura,version_dotnet,procesadores_logicos,barrido,workers,repeticiones,mediana_ms,minimo_ms,maximo_ms,speedup_absoluto,speedup_relativo,eficiencia,karp_flatt");
        AppendRows(csv, report, "sequential", new[]
        {
            new BenchmarkConfigurationSummary(
                1,
                report.SequentialMinimumMilliseconds,
                report.SequentialMaximumMilliseconds,
                report.SequentialMedianMilliseconds,
                1,
                1,
                1,
                null)
        });
        AppendRows(csv, report, "io", report.IoSweep);
        AppendRows(csv, report, "cpu", report.CpuSweep);
        await File.WriteAllTextAsync(path, csv.ToString(), Encoding.UTF8, cancellationToken);

        static void AppendRows(
            StringBuilder output,
            PipelineBenchmarkReport report,
            string sweep,
            IReadOnlyList<BenchmarkConfigurationSummary> rows)
        {
            foreach (var row in rows)
            {
                output.AppendLine();
                output.Append(EscapeCsv(report.OperatingSystem)).Append(',')
                    .Append(EscapeCsv(report.ProcessArchitecture)).Append(',')
                    .Append(EscapeCsv(report.DotnetVersion)).Append(',')
                    .Append(report.LogicalProcessors.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(sweep).Append(',')
                    .Append(row.Workers.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Repetitions.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.MedianMilliseconds.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.MinimumMilliseconds.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.MaximumMilliseconds.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.AbsoluteSpeedup.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.RelativeSpeedup.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.Efficiency.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.KarpFlatt?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }

        static string EscapeCsv(string value) =>
            "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

public sealed record PipelineBenchmarkReport(
    DateTimeOffset GeneratedAt,
    int Repetitions,
    int LogicalProcessors,
    string OperatingSystem,
    string ProcessArchitecture,
    string DotnetVersion,
    double SequentialMinimumMilliseconds,
    double SequentialMaximumMilliseconds,
    double SequentialMedianMilliseconds,
    IReadOnlyList<BenchmarkConfigurationSummary> IoSweep,
    IReadOnlyList<BenchmarkConfigurationSummary> CpuSweep);

public sealed record BenchmarkConfigurationSummary(
    int Workers,
    double MinimumMilliseconds,
    double MaximumMilliseconds,
    double MedianMilliseconds,
    double AbsoluteSpeedup,
    double RelativeSpeedup,
    double Efficiency,
    double? KarpFlatt);
