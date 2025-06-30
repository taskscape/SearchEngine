using System.Text.Json.Serialization;
using WeaviateNET;

namespace SearchEngineServer.Models;

public class IndexData
{
    [JsonInclude]
    [JsonPropertyName("fileName")] 
    public string? FileName;

    [JsonInclude]
    [JsonPropertyName("filePath")] 
    public string? FilePath;

    [JsonInclude]
    [JsonPropertyName("content")] 
    public string? Content;

    [JsonInclude]
    [JsonPropertyName("previewIcon")] 
    [field: IndexFilterable(false)] 
    [field: IndexSearchable(false)]
    public string? PreviewIcon;

    [JsonInclude]
    [JsonPropertyName("lastModified")] 
    public DateTime LastModified;

    [JsonInclude]
    [JsonPropertyName("created")] 
    public DateTime Created;

    [JsonInclude]
    [JsonPropertyName("uid")] 
    public Guid Uid;
}