using System.Security.Cryptography;

namespace HoldTheLine.Server;

/// <summary>
/// Session-level identifiers (plan §5.3). These are NOT the deterministic match RNG — they are
/// one-off, security-sensitive tokens, so they use the crypto RNG. The match seed itself is also
/// drawn here once at match creation, then handed to the rules layer's deterministic RNG.
/// </summary>
public static class SessionAuth
{
    // Room codes omit easily-confused characters (0/O, 1/I) for read-aloud sharing.
    private const string RoomAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public static string NewRoomCode(int length = 6)
    {
        Span<char> code = stackalloc char[length];
        for (int i = 0; i < length; i++)
            code[i] = RoomAlphabet[RandomNumberGenerator.GetInt32(RoomAlphabet.Length)];
        return new string(code);
    }

    /// <summary>128-bit URL-safe token; the only credential that re-attaches a dropped player to a match.</summary>
    public static string NewResumeToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Server-issued guest id when a client connects without one.</summary>
    public static string NewGuestId() => "guest-" + NewResumeToken()[..12];

    /// <summary>Fresh match seed for the deterministic rules RNG.</summary>
    public static ulong NewMatchSeed()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return BitConverter.ToUInt64(b);
    }

    /// <summary>Unbiased coin flip for which seat takes the first turn.</summary>
    public static int NewFirstSeat() => RandomNumberGenerator.GetInt32(2);
}
