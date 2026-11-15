using System.Threading.Channels;
using RadarOfertasMultinucleo.Configuration;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Pipeline;

public sealed class PipelineChannels
{
    public Channel<GameRecord> DiscoveryToDownload { get; }
    public Channel<SourceResponse> DownloadToParse { get; }
    public Channel<ParsedCandidate> ParseToMatch { get; }
    public Channel<MatchedOffer> MatchToPersist { get; }
    public Channel<PersistedOffer> PersistToPublish { get; }

    public PipelineChannels(PipelineOptions options)
    {
        options.Validate();
        DiscoveryToDownload = CreateBounded<GameRecord>(options.QueueSizeStage1);
        DownloadToParse = CreateBounded<SourceResponse>(options.QueueSizeStage2);
        ParseToMatch = CreateBounded<ParsedCandidate>(options.QueueSizeStage3);
        MatchToPersist = CreateBounded<MatchedOffer>(options.QueueSizeStage4);
        PersistToPublish = CreateBounded<PersistedOffer>(options.QueueSizeStage5);
    }

    private static Channel<T> CreateBounded<T>(int capacity) =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
}
