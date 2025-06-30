namespace SearchEngineAgents.Helpers;

public static class FileId
{
    private static readonly Guid Ns =
        Guid.Parse("b4a24939-f398-5d2b-8969-1a4af4a3a400");

    public static Guid FromPath(string fullPath)
    {
        string canonical = Path.GetFullPath(fullPath)
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToLowerInvariant();
        byte[] nsAndName = Ns.ToByteArray()
            .Concat(System.Text.Encoding.UTF8.GetBytes(canonical))
            .ToArray();
        byte[] hash = System.Security.Cryptography.SHA1.HashData(nsAndName);
        
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash[..16]);
    }
}