using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SearchEngineMCP;

public record SearchHitDto(
    string uid,
    string title,
    string content,
    DateTime timestamp,
    double score,
    int source);

public record SearchPageDto([property: JsonPropertyName("results")] SearchHitDto[] Results);
public record SearchResultDto([property: JsonPropertyName("results")] SearchHitDto Result);

public interface IVectorSearchClient
{
    Task<SearchPageDto> SearchAsync(string query, int page, int size);
    Task<SearchResultDto> GetFileAsync(string uid);
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
    
    public async Task<SearchResultDto> GetFileAsync(string uid)
    {
        HttpResponseMessage resp = await http.GetAsync($"files/{Uri.EscapeDataString(uid)}");

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SearchResultDto>()
               ?? new SearchResultDto(null);

    }
}
