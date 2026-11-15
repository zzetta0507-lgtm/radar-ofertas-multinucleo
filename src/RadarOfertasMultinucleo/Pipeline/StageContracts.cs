using System.Threading.Channels;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Pipeline;

public interface IDiscoveryStage
{
    Task RunAsync(ChannelWriter<GameRecord> output, CancellationToken cancellationToken);
}

public interface IDownloadStage
{
    Task RunAsync(
        ChannelReader<GameRecord> input,
        ChannelWriter<SourceResponse> output,
        CancellationToken cancellationToken);
}

public interface IParseStage
{
    Task RunAsync(
        ChannelReader<SourceResponse> input,
        ChannelWriter<ParsedCandidate> output,
        CancellationToken cancellationToken);
}

public interface IMatchStage
{
    Task RunAsync(
        ChannelReader<ParsedCandidate> input,
        ChannelWriter<MatchedOffer> output,
        CancellationToken cancellationToken);
}

public interface IPersistStage
{
    Task RunAsync(
        ChannelReader<MatchedOffer> input,
        ChannelWriter<PersistedOffer> output,
        CancellationToken cancellationToken);
}

public interface IPublishStage
{
    Task RunAsync(
        ChannelReader<PersistedOffer> input,
        CancellationToken cancellationToken);
}
