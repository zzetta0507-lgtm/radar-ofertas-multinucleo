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
    string CanonicalTitle,
    IReadOnlyList<string> AlternativeTitles,
    IReadOnlyList<string> Platforms,
    DateOnly? ReleaseDate,
    decimal? Msrp,
    string? Currency,
    GameType Type);

public sealed record SourceResponse(
    Guid GameId,
    string SourceId,
    string? ProductUrl,
    string ContentType,
    string? Body,
    DateTimeOffset RetrievedAt,
    bool IsSuccessful,
    string? FailureReason);

public sealed record ParsedCandidate(
    Guid GameId,
    string SourceId,
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
    ParsedCandidate Candidate,
    double Similarity,
    bool IsUncertain,
    bool IsDeal,
    decimal? DiscountPercent);

public sealed record PersistedOffer(
    long ObservationId,
    Guid GameId,
    string SourceId,
    string? ProductUrl,
    decimal? CurrentPrice,
    decimal? RegularPrice,
    string? Currency,
    OfferFormat Format,
    bool IsDeal,
    decimal? DiscountPercent,
    DateTimeOffset CapturedAt);
