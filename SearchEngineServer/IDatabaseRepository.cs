using SearchEngineServer.Models;

namespace SearchEngineServer;

public interface IDatabaseRepository
{
    Task<IEnumerable<SearchHit>> FindMatches(string query, int limit);
    Task<bool> AddIndex(IndexData index);
    Task<bool> DeleteByUidAsync(Guid uid, string? tenant = null, bool treat404AsSuccess = false);
    Task<bool> BatchAddIndexesAsync(IEnumerable<IndexData> items, CancellationToken cancellation);
    Task<bool> BatchAddEmailsAsync(IEnumerable<EmailData> items, CancellationToken cancellation);
    Task<bool> CheckIfIndexExists(Guid uid);
}