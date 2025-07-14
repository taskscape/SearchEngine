namespace SearchEngineAgents.Models;

public class FileStamp
{
    public Guid Id { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public string Path { get; set; } = string.Empty;
}