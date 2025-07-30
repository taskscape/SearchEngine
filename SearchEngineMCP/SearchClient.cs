using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SearchEngineMCP;

public record SearchHitDto(
    string uid,
    string title,
    string content,
    DateTime timestamp,
    double score);

public record SearchPageDto([property: JsonPropertyName("results")] SearchHitDto[] Results);

public interface IVectorSearchClient
{
    Task<SearchPageDto> SearchAsync(string query, int page, int size);
}

public class VectorSearchClient(HttpClient http) : IVectorSearchClient
{
    public async Task<SearchPageDto> SearchAsync(string query, int page, int size)
    {
        HttpResponseMessage resp = await http.GetAsync(
            $"search/{Uri.EscapeDataString(query)}?page={page}&hitsPerPage={size}");

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SearchPageDto>()
               ?? new SearchPageDto([]);
    }
}
