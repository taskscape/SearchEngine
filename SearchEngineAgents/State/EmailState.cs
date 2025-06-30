using LiteDB;

namespace SearchEngineAgents.State;

public sealed class EmailState : IDisposable
{
    private readonly LiteDatabase _db = new("Filename=emailstate.db;Mode=Shared");
    private readonly ILiteCollection<Stamp> _stamps;

    public EmailState()
    {
        _stamps = _db.GetCollection<Stamp>("emails");
        _stamps.EnsureIndex(x => x.Id, unique: true);
    }

    public bool IsUnchanged(Guid id, DateTimeOffset? sent)
        => _stamps.FindById(id) is { Sent: var s } && s.Equals(sent);

    public void Touch(Guid id, DateTimeOffset? sent)
        => _stamps.Upsert(new Stamp { Id = id, Sent = sent });

    public IEnumerable<Guid> AllIds()
        => _stamps.Query().Select(x => x.Id).ToEnumerable();

    public void Delete(Guid id) => _stamps.Delete(id);

    public void Dispose() => _db.Dispose();

    private class Stamp
    {
        public Guid Id   { get; set; }
        public DateTimeOffset? Sent { get; set; }
    }
}