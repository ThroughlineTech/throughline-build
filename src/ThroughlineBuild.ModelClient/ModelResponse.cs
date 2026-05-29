namespace ThroughlineBuild.ModelClient;

public record ModelResponse(
    IReadOnlyList<ContentBlock> Content,
    string StopReason,
    string Model,
    Usage Usage
);

public record Usage(
    int InputTokens,
    int OutputTokens,
    int? CacheReadTokens,
    int? CacheCreateTokens,
    string Model,
    string Vendor,
    decimal? Cost
);

public abstract record ModelStreamEvent;

public record MessageStartEvent(string Model) : ModelStreamEvent;

public record ContentDeltaEvent(int Index, ContentBlock Delta) : ModelStreamEvent;

public record MessageDeltaEvent(string? StopReason, UsageDelta? Usage) : ModelStreamEvent;

public record MessageStopEvent() : ModelStreamEvent;

public record ErrorEvent(string Message) : ModelStreamEvent;

public record UsageDelta(int OutputTokens);
