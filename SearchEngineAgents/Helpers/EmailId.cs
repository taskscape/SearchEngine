using System.Security.Cryptography;
using System.Text;

namespace SearchEngineAgents.Helpers;

public static class EmailId
{
    private static readonly Guid Ns =
        Guid.Parse("3e8ad8d7-2fe2-4d5e-acf4-b9aa18c5b624");

    public static Guid FromMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            messageId = $"local-{Guid.NewGuid()}";

        byte[] raw  = Ns.ToByteArray().Concat(Encoding.UTF8.GetBytes(messageId)).ToArray();
        byte[] hash = SHA1.HashData(raw);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash[..16]);
    }
}