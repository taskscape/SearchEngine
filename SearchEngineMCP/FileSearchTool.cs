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
        string Snippet,
        DateTime Timestamp,
        double Score);

    public record SearchResult(IEnumerable<SearchHit> Content);

    [McpServerTool]
    [Description("Search over the files indexed on the machine. Returns up to page_size snippets so the model can decide what to read.")]
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
            h.score));

        return new SearchResult(hits);
    }
}
