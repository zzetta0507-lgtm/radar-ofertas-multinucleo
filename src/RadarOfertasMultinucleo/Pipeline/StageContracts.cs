using System.Threading.Channels;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Pipeline;

public interface IDiscoveryStage
{
    Task<IReadOnlyList<GameRecord>> DiscoverAsync(CancellationToken cancellationToken);

    Task RunAsync(
        ChannelWriter<GameRecord> output,
        CancellationToken cancellationToken);
}

public interface IDownloadStage
{
    Task<IReadOnlyList<SourceResponse>> DownloadAsync(
        GameRecord game,
        CancellationToken cancellationToken);
}

public interface IParseStage
{
    Task<ParsedCandidate?> ParseAsync(
        SourceResponse response,
        CancellationToken cancellationToken);
}

public interface IMatchStage
{
    void ConfigureParallelism(int maxDegreeOfParallelism);

    Task<MatchedOffer?> MatchAsync(
        ParsedCandidate candidate,
        CancellationToken cancellationToken);
}

public interface IPersistStage
{
    Task<PersistedOffer?> PersistAsync(
        MatchedOffer offer,
        CancellationToken cancellationToken);
}

public interface IPublishStage
{
    Task PublishAsync(
        PersistedOffer offer,
        CancellationToken cancellationToken);

    Task CompleteAsync(CancellationToken cancellationToken);
}
