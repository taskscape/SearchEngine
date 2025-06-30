using SearchEngineAgents.Models;

namespace SearchEngineAgents.Agents;

public interface IFileExtractionAgent
{
    IEnumerable<string> SupportedExtensions { get; }
    Task<IndexData?> ExtractAsync(string filePath, CancellationToken ct);
    Task<byte[]?> GeneratePreviewImageAsync(string filePath, CancellationToken ct);
}