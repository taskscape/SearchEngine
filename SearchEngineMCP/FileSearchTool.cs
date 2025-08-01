using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SearchEngineMCP;

[McpServerToolType]
public class FileSearchTools
{
    private readonly IVectorSearchClient _api;
    
    public FileSearchTools(IVectorSearchClient api) => _api = api;

    public record SearchHit(
        string Uid,
        string Title,
        string Content,
        DateTime Timestamp,
        double Score,
        int Source);

    public record SearchResult(IEnumerable<SearchHit> Content);

    [McpServerTool]
    [Description("Search for files matching the query indexed on the machine by their filename, filepath, content and timestamp. Returns up to page_size results so the model can decide what to read. These results contain information such as a unique identifier, path (title), cropped content, timestamp and whether it's a file or email (source - 0 = file, 1 = email).")]
    public async Task<SearchResult> search_files(
        string query,
        int page = 0,
        [Description("Maximum 50")] int pageSize = 10)
    {
        SearchPageDto pageDto = await _api.SearchAsync(query, page, pageSize);

        IEnumerable<SearchHit> hits = pageDto.Results.Select(h => new SearchHit(
            h.uid,
            h.title,
            h.content,
            h.timestamp,
            h.score,
            h.source));

        return new SearchResult(hits);
    }
    
    [McpServerTool]
    [Description("Search for a file or email by it's unique identifier. Returns a result with the unique identifier, filepath (title), full uncropped content, a timestamp and information whether it is a file or an email (source - 0 = file, 1 = email).")]
    public async Task<SearchHit> get_full_file(string uid)
    {
        SearchResultDto result = await _api.GetFileAsync(uid);
        SearchHitDto file = result.Result;
        return new SearchHit(file.uid, file.title, file.content, file.timestamp, file.score, file.source);
    }
}
