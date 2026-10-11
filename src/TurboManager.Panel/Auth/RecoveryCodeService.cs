using System.Security.Cryptography;
using System.Text;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

public static class RecoveryCodeService
{
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public const int CodeCount = 10;

    /// <summary>Replaces the user's recovery codes and returns the new plain codes (shown once).</summary>
    public static IReadOnlyList<string> Regenerate(PanelUser user)
    {
        var codes = Enumerable.Range(0, CodeCount).Select(_ => $"{RandomChunk()}-{RandomChunk()}").ToList();

        user.RecoveryCodes.Clear();
        user.RecoveryCodes.AddRange(codes.Select(c => new RecoveryCode { CodeHash = Hash(c) }));

        return codes;
    }

    /// <summary>Marks a matching unused code as used. Returns false if none matches.</summary>
    public static bool TryRedeem(PanelUser user, string? code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var hash = Hash(code);
        var match = user.RecoveryCodes.FirstOrDefault(c => c.UsedAt is null && c.CodeHash == hash);

        if (match is null)
        {
            return false;
        }

        match.UsedAt = now;

        return true;
    }

    public static string Hash(string code)
    {
        var normalized = new string(code.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string RandomChunk() => RandomNumberGenerator.GetString(Alphabet, 5);
}
