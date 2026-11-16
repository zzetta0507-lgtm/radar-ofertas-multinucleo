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
    string? FailureReason);

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
    DateTimeOffset RetrievedAt);

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
    bool IsUncertainMatch);

public sealed record TitleMatchCandidate(
    Guid GameId,
    string CanonicalTitle,
    IReadOnlyList<string> DifficultCaseTags);
