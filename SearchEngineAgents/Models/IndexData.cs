using System.Text.Json.Serialization;
using WeaviateNET;

namespace SearchEngineAgents.Models;

public class IndexData
{
    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }
    
    [JsonPropertyName("filePath")]
    public string? FilePath  { get; set; }

    [JsonPropertyName("content")]
    public string? Content  { get; set; }

    [JsonPropertyName("previewIcon")]
    [field: IndexFilterable(false)]
    [field: IndexSearchable(false)]
    public string? PreviewIcon { get; set; }
    
    [JsonPropertyName("lastModified")]
    public DateTime LastModified { get; set; }
    
    [JsonPropertyName("created")]
    public DateTime Created { get; set; }
    
    [JsonPropertyName("uid")] 
    public Guid Uid { get; set; }
}