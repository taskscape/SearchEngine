using System.Text.Json.Serialization;
using WeaviateNET;

namespace SearchEngineAgents.Models;

public class EmailData
{
    [JsonPropertyName("uid")]
    public Guid Uid { get; set; }

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("sent")]
    public DateTimeOffset? Sent { get; set; }
    
    [JsonPropertyName("previewIcon")]
    [field: IndexFilterable(false)]
    [field: IndexSearchable(false)]
    public string? PreviewIcon { get; set; }
}