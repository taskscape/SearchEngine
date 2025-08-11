namespace SearchEngineClient.Models;

public enum SearchSource { File, Email }

public sealed record SearchHit
(
    Guid Uid,
    string Title,
    string? Content,
    DateTimeOffset? Timestamp,
    float Score,
    SearchSource Source,
    string PreviewIcon,
    string? DownloadPath
);

public sealed record SearchResponse(IEnumerable<SearchHit> Results);