using LiteDB;

namespace SearchEngineAgents.State;

public sealed class ScanState : IDisposable
{
    private readonly LiteDatabase _db = new("Filename=scanstate.db;Mode=Shared");
    private readonly ILiteCollection<FileStamp> _stamps;

    public ScanState()
    {
        _stamps = _db.GetCollection<FileStamp>("stamps");
        _stamps.EnsureIndex(x => x.Id, true);
    }

    public bool IsUnchanged(Guid id, DateTimeOffset currentMTimeUtc)
    {
        DateTimeOffset? stored = _stamps.FindById(id)?.LastWriteUtc;
        return stored != null &&
               Math.Abs((stored.Value - currentMTimeUtc).TotalMilliseconds) < 1;
    }

    public void Touch(Guid id, DateTimeOffset mTimeUtc)
        => _stamps.Upsert(new FileStamp { Id = id, LastWriteUtc = mTimeUtc });

    public IEnumerable<Guid> AllIds()
        => _stamps.Query()
            .Select(x => x.Id)
            .ToEnumerable();  

    public void Delete(Guid id) => _stamps.Delete(id);

    public void Dispose() => _db.Dispose();

    private class FileStamp
    {
        public Guid Id { get; set; }
        public DateTimeOffset LastWriteUtc { get; set; }
    }
}