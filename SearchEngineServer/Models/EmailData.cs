using System.Text.Json.Serialization;
using WeaviateNET;

namespace SearchEngineServer.Models;

public class EmailData
{
    [JsonInclude]
    [JsonPropertyName("uid")] public Guid Uid;

    [JsonInclude]
    [JsonPropertyName("subject")] public string? Subject;

    [JsonInclude]
    [JsonPropertyName("body")] public string? Body;

    [JsonInclude]
    [JsonPropertyName("from")] public string? From;

    [JsonInclude]
    [JsonPropertyName("sent")] public DateTime Sent;

    [JsonInclude]
    [JsonPropertyName("previewIcon")] 
    [field: IndexFilterable(false)] 
    [field: IndexSearchable(false)]
    public string? PreviewIcon;
}