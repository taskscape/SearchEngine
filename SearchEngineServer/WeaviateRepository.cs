using System.Net;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using SearchEngineServer.Models;
using WeaviateNET;

namespace SearchEngineServer;

public class WeaviateRepository(ILogger<WeaviateRepository> logger, IConfiguration configuration) : IDatabaseRepository
{
    private const string FileCollectionName = "Index_data";
    private const string EmailCollectionName = "Email_data";
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 8
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestVersion = HttpVersion.Version11
    };

    public async Task<IEnumerable<SearchHit>> FindMatches(
        string query,
        int limit,
        int offset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        WeaviateDB? weaviate = await GetWeaviateDbAsync();
        if (weaviate is null)
        {
            logger.LogError("Weaviate endpoint or API key is missing");
            return Array.Empty<SearchHit>();
        }
        int hitsToSkip = offset * limit;

        string gql = $$"""
                       {
                         Get {
                           Index_data (
                             limit:  50
                             nearText: { concepts: [ "{{query}}" ] }
                           ){
                             filePath content created uid previewIcon
                             _additional { rerank(property:"content", query:"{{query}}"){ score } }
                           }
                           Email_data (
                             limit:  50
                             nearText: { concepts: [ "{{query}}" ] }
                           ){
                             subject body sent uid previewIcon
                             _additional { rerank(property:"body", query:"{{query}}"){ score } }
                           }
                         }
                       }
                       """;

        GraphQLResponse r = await weaviate.Schema.RawQuery(new GraphQLQuery { Query = gql });

        List<SearchHit> hits = [];

        foreach (JToken t in (JArray?)r.Data?["Get"]?["Index_data"] ?? [])
        {
            hits.Add(new SearchHit(
                Uid:         Guid.Parse((string?)t["uid"] ?? Guid.Empty.ToString()),
                Title:       (string?)t["filePath"] ?? string.Empty,
                Content:     CropWithEllipsis(t["content"]?.ToString()),
                Timestamp:   t.Value<DateTime?>("created"),
                Score:       (float?)t.SelectToken("_additional.rerank[0].score") ?? 0,
                PreviewIcon: (string?)t["previewIcon"] ?? string.Empty,
                Source:      SearchSource.File));
        }

        foreach (JToken t in (JArray?)r.Data?["Get"]?["Email_data"] ?? [])
        {
            hits.Add(new SearchHit(
                Uid:         Guid.Parse((string?)t["uid"] ?? Guid.Empty.ToString()),
                Title:       (string?)t["subject"] ?? string.Empty,
                Content:     CropWithEllipsis(t["body"]?.ToString()),
                Timestamp:   t.Value<DateTime?>("sent"),
                Score:       (float?)t.SelectToken("_additional.rerank[0].score") ?? 0,
                PreviewIcon: (string?)t["previewIcon"] ?? string.Empty,
                Source:      SearchSource.Email));
        }
        
        return hits
            .OrderByDescending(h => h.Score)
            .Skip(hitsToSkip)
            .Take(limit)
            .ToList()
            .AsReadOnly();
    }

    public async Task<bool> AddIndex(IndexData index)
    {
        if (index is null)
        {
            return false;
        }
        
        WeaviateDB? weaviate;
        try
        {
            weaviate = await GetWeaviateDbAsync();
        }
        catch (Exception ex)
        {
            logger.LogError("Could not connect to Weaviate: {Message}", ex.Message);
            return false;
        }
        
        try
        {
            if (weaviate == null)
            {
                logger.LogError("Could not connect to Weaviate");
                return false;
            }
            await weaviate.Schema.Update();

            WeaviateClass<IndexData> txt = await GetOrCreateFileClass(weaviate);

            WeaviateObject<IndexData> doc = txt.Create();
            doc.Id = index.Uid;
            doc.Properties = index;

            await txt.Add(doc);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError("An error occured when trying to add an index: {Message}", ex.Message);
            return false;
        }
    }
    
    public async Task<bool> DeleteByUidAsync(Guid uid, string? tenant = null, bool treat404AsSuccess = false)
    {
        try
        {
            WeaviateDB db = await GetWeaviateDbAsync()
                            ?? throw new InvalidOperationException("DB not configured");
            
            string baseUrl = db.Client.BaseUrl.TrimEnd('/');
            if (!baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl += "/v1";
            }
            
            string uri = $"{baseUrl}/objects/{uid}";
            if (tenant is not null) uri += $"?tenant={Uri.EscapeDataString(tenant)}";
            using HttpResponseMessage resp = await Http.DeleteAsync(uri);

            if (resp.StatusCode == HttpStatusCode.NotFound && treat404AsSuccess)
                return true;

            if (resp.IsSuccessStatusCode) return true;
            string body = await resp.Content.ReadAsStringAsync();
            logger.LogError("Weaviate returned {Code}: {Body}", resp.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteByUid failed for {Uid}", uid);
            return false;
        }
    }

    public async Task<bool> BatchAddIndexesAsync(
        IEnumerable<IndexData> items,
        CancellationToken cancellation = default)
    {
        int batchSize = int.Parse(configuration["Batching:MaxBatchSize"] ?? "32");
        if (items is null) return false;
        
        WeaviateDB weaviate;
        try
        {
            weaviate = await GetWeaviateDbAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not connect to Weaviate");
            return false;
        }

        WeaviateClass<IndexData> collection = await GetOrCreateFileClass(weaviate);
        string className = collection.Name;
        string baseUrl = weaviate.Client.BaseUrl.TrimEnd('/');
        
        foreach (IndexData[] chunk in items.Chunk(batchSize))
        {
            var payload = new
            {
                objects = chunk.Select(d => new
                {
                    id         = d.Uid,
                    @class     = className,
                    properties = d
                })
            };

            using HttpRequestMessage req = new(HttpMethod.Post, $"{baseUrl}/batch/objects");
            req.Content = JsonContent.Create(payload);

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, cancellation);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Transport error while calling Weaviate; will retry");
                return false;
            }
            catch (TaskCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                logger.LogWarning(ex, "Timeout while calling Weaviate; will retry");
                return false;
            }

            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(cancellation);
                logger.LogError("Batch insert failed (HTTP {Status}): {Body}",
                    resp.StatusCode, body);
                return false;
            }
            
            string bodyJson = await resp.Content.ReadAsStringAsync(cancellation);
            if (string.IsNullOrWhiteSpace(bodyJson))
                continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(bodyJson);

                JsonElement objectsEl = GetObjectsArray(doc.RootElement);
                foreach (JsonElement obj in objectsEl.EnumerateArray().Where(obj => obj.ValueKind == JsonValueKind.Object))
                {
                    if (!obj.TryGetProperty("result", out JsonElement resultEl) ||
                        resultEl.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!resultEl.TryGetProperty("errors", out JsonElement errsEl))
                        continue;
                    
                    if (errsEl.ValueKind == JsonValueKind.Object &&
                        errsEl.TryGetProperty("error", out JsonElement arrEl) &&
                        arrEl.ValueKind == JsonValueKind.Array &&
                        arrEl.GetArrayLength() > 0)
                    {
                        string msg = arrEl[0].TryGetProperty("message", out JsonElement m)
                            ? m.GetString() ?? "<no message>"
                            : "<no message>";
                        logger.LogError("Weaviate batch error: {Msg}", msg);
                        return false;
                    }

                    if (errsEl.ValueKind != JsonValueKind.Array ||
                        errsEl.GetArrayLength() <= 0) continue;
                    {
                        string msg = errsEl[0].TryGetProperty("message", out JsonElement m)
                            ? m.GetString() ?? "<no message>"
                            : "<no message>";
                        logger.LogError("Weaviate batch error: {Msg}", msg);
                        return false;
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Unexpected batch payload: {Payload}", bodyJson);
            }
        }

        return true;
    }
    
    public async Task<bool> BatchAddEmailsAsync(
        IEnumerable<EmailData> items,
        CancellationToken cancellation = default)
    {
        int batchSize = int.Parse(configuration["Batching:MaxBatchSize"] ?? "32");
        if (items is null) return false;
        
        WeaviateDB weaviate;
        try
        {
            weaviate = await GetWeaviateDbAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not connect to Weaviate");
            return false;
        }

        WeaviateClass<IndexData> collection = await GetOrCreateEmailClass(weaviate);
        string className = collection.Name;
        string baseUrl = weaviate.Client.BaseUrl.TrimEnd('/');
        
        foreach (EmailData[] chunk in items.Chunk(batchSize))
        {
            var payload = new
            {
                objects = chunk.Select(d => new
                {
                    id         = d.Uid,
                    @class     = className,
                    properties = d
                })
            };

            using HttpRequestMessage req = new(HttpMethod.Post, $"{baseUrl}/batch/objects");
            req.Content = JsonContent.Create(payload);

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, cancellation);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Transport error while calling Weaviate; will retry");
                return false;
            }
            catch (TaskCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                logger.LogWarning(ex, "Timeout while calling Weaviate; will retry");
                return false;
            }

            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(cancellation);
                logger.LogError("Batch insert failed (HTTP {Status}): {Body}",
                    resp.StatusCode, body);
                return false;
            }
            
            string bodyJson = await resp.Content.ReadAsStringAsync(cancellation);
            if (string.IsNullOrWhiteSpace(bodyJson))
                continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(bodyJson);

                JsonElement objectsEl = GetObjectsArray(doc.RootElement);
                foreach (JsonElement obj in objectsEl.EnumerateArray().Where(obj => obj.ValueKind == JsonValueKind.Object))
                {
                    if (!obj.TryGetProperty("result", out JsonElement resultEl) ||
                        resultEl.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!resultEl.TryGetProperty("errors", out JsonElement errsEl))
                        continue;
                    
                    if (errsEl.ValueKind == JsonValueKind.Object &&
                        errsEl.TryGetProperty("error", out JsonElement arrEl) &&
                        arrEl.ValueKind == JsonValueKind.Array &&
                        arrEl.GetArrayLength() > 0)
                    {
                        string msg = arrEl[0].TryGetProperty("message", out JsonElement m)
                            ? m.GetString() ?? "<no message>"
                            : "<no message>";
                        logger.LogError("Weaviate batch error: {Msg}", msg);
                        return false;
                    }

                    if (errsEl.ValueKind != JsonValueKind.Array ||
                        errsEl.GetArrayLength() <= 0) continue;
                    {
                        string msg = errsEl[0].TryGetProperty("message", out JsonElement m)
                            ? m.GetString() ?? "<no message>"
                            : "<no message>";
                        logger.LogError("Weaviate batch error: {Msg}", msg);
                        return false;
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Unexpected batch payload: {Payload}", bodyJson);
            }
        }

        return true;
    }

    public async Task<bool> CheckIfIndexExists(Guid uid)
    {
        WeaviateDB? weaviate = await GetWeaviateDbAsync();
        if (weaviate is null)
        {
            logger.LogError("Weaviate endpoint or API key is missing");
            return false;
        }

        try
        {
            string url = $"{weaviate.Client.BaseUrl.TrimEnd('/')}/objects/{uid}";
            using HttpResponseMessage resp = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Weaviate check failed for Uid {Uid}", uid);
            return false;
        }
    }

    private static JsonElement GetObjectsArray(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                return root;
            case JsonValueKind.Object:
                if (root.TryGetProperty("objects", out JsonElement objs))
                    return objs;
                if (root.TryGetProperty("results", out JsonElement res) &&
                    res.ValueKind == JsonValueKind.Object &&
                    res.TryGetProperty("objects", out JsonElement objs2))
                    return objs2;
                break;
        }
        
        using JsonDocument empty = JsonDocument.Parse("[]");
        return empty.RootElement;
    }

    private static async Task<WeaviateClass<IndexData>> GetOrCreateFileClass(WeaviateDB weaviate)
    {
        WeaviateClass<IndexData>? txt = weaviate.Schema.GetClass<IndexData>(FileCollectionName);
        if (txt is not null) return txt;
        
        txt = await weaviate.Schema.NewClass<IndexData>(FileCollectionName);
        
        return txt ?? throw new InvalidOperationException(
            $"Failed to create or retrieve Weaviate class '{FileCollectionName}'.");
    }
    
    private static async Task<WeaviateClass<IndexData>> GetOrCreateEmailClass(WeaviateDB weaviate)
    {
        WeaviateClass<IndexData>? txt = weaviate.Schema.GetClass<IndexData>(EmailCollectionName);
        if (txt is not null) return txt;
        
        txt = await weaviate.Schema.NewClass<IndexData>(EmailCollectionName);
        
        return txt ?? throw new InvalidOperationException(
            $"Failed to create or retrieve Weaviate class '{EmailCollectionName}'.");
    }

    private async Task<WeaviateDB?> GetWeaviateDbAsync()
    {
        string? endpoint = configuration["Weaviate:Endpoint"];
        string? apiKey   = configuration["Weaviate:ApiKey"];

        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        WeaviateDB weaviate = string.IsNullOrWhiteSpace(apiKey)
            ? new WeaviateDB(endpoint)
            : new WeaviateDB(endpoint, apiKey);

        await weaviate.Schema.Update();
        return weaviate;
    }
    
    private static string CropWithEllipsis(string? input, int maxLength = 400)
    {
        const string ellipsis = "...";

        if (string.IsNullOrEmpty(input) || input!.Length <= maxLength)
        {
            return input ?? string.Empty;
        }
        
        int sliceLength = Math.Max(0, maxLength - ellipsis.Length);
        return string.Concat(input.AsSpan(0, sliceLength), ellipsis);
    }
}