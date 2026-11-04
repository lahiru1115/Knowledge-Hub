using System.Security.Cryptography;
using System.Text;
using KnowledgeHub.Application.Common;
using Konscious.Security.Cryptography;

namespace KnowledgeHub.Infrastructure.Security;

/// <summary>
/// Argon2id with the OWASP minimum (19 MiB, 2 iterations, parallelism 1; Doc 02 §9.2). The parameters are
/// stored in the hash so they can be raised later; <see cref="NeedsRehash"/> flags older hashes.
/// Format: <c>$argon2id$v=19$m=19456,t=2,p=1$salt$hash</c> (Base64, no padding).
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const int MemoryKib = 19 * 1024;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, MemoryKib, Iterations, Parallelism, HashBytes);
        return $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public bool Verify(string password, string hash)
    {
        if (!TryParse(hash, out var m, out var t, out var p, out var salt, out var expected)) return false;
        var actual = Compute(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool NeedsRehash(string hash) =>
        !TryParse(hash, out var m, out var t, out var p, out _, out _) || m < MemoryKib || t < Iterations || p != Parallelism;

    private static byte[] Compute(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static bool TryParse(string stored, out int m, out int t, out int p, out byte[] salt, out byte[] hash)
    {
        m = t = p = 0; salt = hash = [];
        var parts = stored.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id") return false;
        try
        {
            foreach (var kv in parts[3].Split(','))
            {
                var pair = kv.Split('=');
                switch (pair[0])
                {
                    case "m": m = int.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "t": t = int.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "p": p = int.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture); break;
                }
            }

            salt = Convert.FromBase64String(Pad(parts[4]));
            hash = Convert.FromBase64String(Pad(parts[5]));
            return m > 0 && t > 0 && p > 0 && salt.Length > 0 && hash.Length > 0;
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=');

    private static string Pad(string s) => s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
}
