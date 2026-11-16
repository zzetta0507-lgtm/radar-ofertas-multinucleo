using System.Threading.Channels;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Pipeline;

public sealed class PipelineRunner
{
    private readonly PipelineOptions _options;
    private readonly IDiscoveryStage _discovery;
    private readonly IDownloadStage _download;
    private readonly IParseStage _parse;
    private readonly IMatchStage _match;
    private readonly IPersistStage _persist;
    private readonly IPublishStage _publish;

    public PipelineRunner(
        PipelineOptions options,
        IDiscoveryStage discovery,
        IDownloadStage download,
        IParseStage parse,
        IMatchStage match,
        IPersistStage persist,
        IPublishStage publish)
    {
        _options = options;
        _discovery = discovery;
        _download = download;
        _parse = parse;
        _match = match;
        _persist = persist;
        _publish = publish;
    }

    public Task RunAsync(ExecutionMode mode, CancellationToken cancellationToken) =>
        mode == ExecutionMode.Sequential
            ? RunSequentialAsync(cancellationToken)
            : RunChannelPipelineAsync(mode, cancellationToken);

    private async Task RunSequentialAsync(CancellationToken cancellationToken)
    {
        var channels = new PipelineChannels(_options);
        var games = await _discovery.DiscoverAsync(cancellationToken);

        foreach (var game in games)
        {
            var queuedGame = await TransferAsync(
                channels.DiscoveryToDownload,
                game,
                cancellationToken);

            var responses = await _download.DownloadAsync(queuedGame, cancellationToken);
            foreach (var response in responses)
            {
                var queuedResponse = await TransferAsync(
                    channels.DownloadToParse,
                    response,
                    cancellationToken);
                var candidate = await _parse.ParseAsync(queuedResponse, cancellationToken);
                if (candidate is null)
                {
                    continue;
                }

                var queuedCandidate = await TransferAsync(
                    channels.ParseToMatch,
                    candidate,
                    cancellationToken);
                var matched = await _match.MatchAsync(queuedCandidate, cancellationToken);
                if (matched is null)
                {
                    continue;
                }

                var queuedMatch = await TransferAsync(
                    channels.MatchToPersist,
                    matched,
                    cancellationToken);
                var persisted = await _persist.PersistAsync(queuedMatch, cancellationToken);
                if (persisted is not null)
                {
                    var queuedPersisted = await TransferAsync(
                        channels.PersistToPublish,
                        persisted,
                        cancellationToken);
                    await _publish.PublishAsync(queuedPersisted, cancellationToken);
                }
            }
        }

        CompleteAll(channels);
        await _publish.CompleteAsync(cancellationToken);
    }

    private async Task RunChannelPipelineAsync(
        ExecutionMode mode,
        CancellationToken cancellationToken)
    {
        var channels = new PipelineChannels(_options);
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = pipelineCancellation.Token;
        var tasks = new[]
        {
            RunStageAsync(
                () => _discovery.RunAsync(channels.DiscoveryToDownload.Writer, token),
                channels.DiscoveryToDownload.Writer,
                pipelineCancellation),
            RunWorkersAsync(
                _options.WorkersIo,
                () => ChannelStageWorkers.RunDownloadWorkersAsync(
                    channels.DiscoveryToDownload.Reader,
                    channels.DownloadToParse.Writer,
                    _download,
                    token),
                channels.DownloadToParse.Writer,
                pipelineCancellation),
            RunStageAsync(
                () => ChannelStageWorkers.RunParseWorkerAsync(
                    channels.DownloadToParse.Reader,
                    channels.ParseToMatch.Writer,
                    _parse,
                    token),
                channels.ParseToMatch.Writer,
                pipelineCancellation),
            RunWorkersAsync(
                mode == ExecutionMode.Parallel ? _options.WorkersCpu : 1,
                () => ChannelStageWorkers.RunMatchWorkerAsync(
                    channels.ParseToMatch.Reader,
                    channels.MatchToPersist.Writer,
                    _match,
                    token),
                channels.MatchToPersist.Writer,
                pipelineCancellation),
            RunStageAsync(
                () => ChannelStageWorkers.RunPersistWorkerAsync(
                    channels.MatchToPersist.Reader,
                    channels.PersistToPublish.Writer,
                    _persist,
                    token),
                channels.PersistToPublish.Writer,
                pipelineCancellation),
            RunTerminalStageAsync(
                () => ChannelStageWorkers.RunPublishWorkerAsync(
                    channels.PersistToPublish.Reader,
                    _publish,
                    token),
                pipelineCancellation)
        };

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            CompleteAll(channels);
        }
    }

    private static async Task<T> TransferAsync<T>(
        Channel<T> channel,
        T item,
        CancellationToken cancellationToken)
    {
        await channel.Writer.WriteAsync(item, cancellationToken);
        return await channel.Reader.ReadAsync(cancellationToken);
    }

    private static async Task RunTerminalStageAsync(
        Func<Task> stage,
        CancellationTokenSource cancellation)
    {
        try
        {
            await stage();
        }
        catch
        {
            cancellation.Cancel();
            throw;
        }
    }

    private static async Task RunStageAsync<T>(
        Func<Task> stage,
        ChannelWriter<T> output,
        CancellationTokenSource cancellation)
    {
        Exception? failure = null;
        try
        {
            await stage();
        }
        catch (Exception exception)
        {
            failure = exception;
            cancellation.Cancel();
            throw;
        }
        finally
        {
            output.TryComplete(failure);
        }
    }

    private static async Task RunWorkersAsync<T>(
        int workerCount,
        Func<Task> worker,
        ChannelWriter<T> output,
        CancellationTokenSource cancellation)
    {
        Exception? failure = null;
        try
        {
            var tasks = Enumerable.Range(0, workerCount).Select(_ => RunWorkerAsync()).ToArray();
            await Task.WhenAll(tasks);
        }
        catch (Exception exception)
        {
            failure = exception;
            cancellation.Cancel();
            throw;
        }
        finally
        {
            output.TryComplete(failure);
        }

        async Task RunWorkerAsync()
        {
            try
            {
                await worker();
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }
        }
    }

    private static void CompleteAll(PipelineChannels channels)
    {
        channels.DiscoveryToDownload.Writer.TryComplete();
        channels.DownloadToParse.Writer.TryComplete();
        channels.ParseToMatch.Writer.TryComplete();
        channels.MatchToPersist.Writer.TryComplete();
        channels.PersistToPublish.Writer.TryComplete();
    }
}
