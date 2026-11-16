namespace RadarOfertasMultinucleo.Domain;

public enum GameType
{
    Game,
    GameWithDlc,
    Dlc,
    Other
}

public enum OfferFormat
{
    Physical,
    Digital,
    Unknown
}

public sealed record GameRecord(
    Guid Id,
    string ExternalId,
    string CanonicalTitle,
    IReadOnlyList<string> AlternativeTitles,
    IReadOnlyList<string> Platforms,
    DateOnly? ReleaseDate,
    decimal? Msrp,
    string? Currency,
    GameType Type,
    string SourceUrl,
    string? CoverImageUrl,
    decimal? OpenCriticScore,
    IReadOnlyList<string> DifficultCaseTags);

public sealed record SourceResponse(
    Guid GameId,
    string SourceId,
    string CanonicalTitle,
    IReadOnlyList<string> Platforms,
    string? ProductUrl,
    string ContentType,
    string? Body,
    DateTimeOffset RetrievedAt,
    bool IsSuccessful,
    string? FailureReason,
    decimal? RepositoryMsrp = null);

public sealed record ParsedCandidate(
    Guid GameId,
    string SourceId,
    string CanonicalTitle,
    IReadOnlyList<string> Platforms,
    string CandidateTitle,
    string? ProductUrl,
    decimal? CurrentPrice,
    decimal? RegularPrice,
    string? Currency,
    OfferFormat Format,
    decimal? Rating,
    string? RatingSource,
    int? MainStoryMinutes,
    int? StoryAndExtrasMinutes,
    int? CompletionistMinutes,
    string? CoverImageUrl,
    DateTimeOffset RetrievedAt,
    decimal? RepositoryMsrp = null);

public sealed record MatchedOffer(
    Guid GameId,
    string MatchedCanonicalTitle,
    ParsedCandidate Candidate,
    double Similarity,
    bool IsUncertain,
    bool IsDeal,
    decimal? DiscountPercent);

public sealed record PersistedOffer(
    long ObservationId,
    Guid GameId,
    string SourceId,
    string CanonicalTitle,
    string? ProductUrl,
    decimal? CurrentPrice,
    decimal? RegularPrice,
    string? Currency,
    OfferFormat Format,
    bool IsDeal,
    decimal? DiscountPercent,
    decimal? Rating,
    string? CoverImageUrl,
    IReadOnlyList<string> Platforms,
    DateTimeOffset CapturedAt,
    double MatchingSimilarity,
    bool IsUncertainMatch,
    bool IsHistoricalLow = false,
    bool IsStale = false,
    DateTimeOffset? StaleSince = null);

public sealed record CatalogGameMetadata(
    string GameId,
    string CanonicalTitle,
    string Type,
    string? ReleaseDate,
    decimal? Msrp,
    string? Currency,
    IReadOnlyList<string> Platforms,
    string? CoverImageUrl,
    decimal? OpenCriticScore,
    decimal? MetacriticScore = null,
    int? MainStoryMinutes = null,
    int? StoryAndExtrasMinutes = null,
    int? CompletionistMinutes = null,
    DateTimeOffset? PlaytimeCapturedAt = null);

public sealed record PriceHistoryEntry(
    long ObservationId,
    string GameId,
    string SourceId,
    decimal? CurrentPrice,
    string? Currency,
    OfferFormat Format,
    DateTimeOffset CapturedAt,
    string? ProductUrl,
    decimal? RegularPrice,
    bool IsDeal,
    decimal? DiscountPercent);

public sealed record TitleMatchCandidate(
    Guid GameId,
    string CanonicalTitle,
    IReadOnlyList<string> DifficultCaseTags);
