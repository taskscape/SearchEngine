using MimeKit;
using SearchEngineAgents.Models;

namespace SearchEngineAgents.Agents;

public interface IEmailExtractionAgent
{
    Task<EmailData> ExtractAsync(MimeMessage message, CancellationToken ct);
    Task<byte[]?> GeneratePreviewImageAsync(MimeMessage message, CancellationToken ct = default);
}