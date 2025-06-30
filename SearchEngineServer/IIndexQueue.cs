using SearchEngineServer.Models;

namespace SearchEngineServer;

public interface IIndexQueue
{
    ValueTask EnqueueAsync(IndexData data, CancellationToken ct = default);
    ValueTask EnqueueAsync(EmailData data, CancellationToken ct = default);
}