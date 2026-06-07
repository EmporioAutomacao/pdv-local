using System.Security.Cryptography;
using System.Text;

namespace SyncAgent.Utilities;

public static class DeterministicGuid
{
    public static Guid Create(params string[] parts)
    {
        var input = string.Join('\u001f', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);

        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
}
