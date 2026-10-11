using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TurboManager.Panel.Setup;

/// <summary>
/// The one-time setup token proves the visitor has console access to the server.
/// install.sh (and tm-setup-token) write only its SHA-256 hash; the plain token is printed once in the terminal.
/// </summary>
public sealed class SetupTokenStore(IOptions<PanelOptions> options, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private string HashFile => options.Value.SetupTokenHashFile;

    public bool Exists => File.Exists(HashFile);

    public bool Verify(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !Exists || IsExpired())
        {
            return false;
        }

        var expected = File.ReadAllText(HashFile).Trim().ToLowerInvariant();
        var actual = Hash(token.Trim());

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }

    public void Consume()
    {
        if (Exists)
        {
            File.Delete(HashFile);
        }
    }

    /// <summary>Creates a new token, stores its hash and returns the plain value.</summary>
    public string Generate()
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(HashFile))!);
        File.WriteAllText(HashFile, Hash(token) + "\n");

        return token;
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private bool IsExpired() => time.GetUtcNow() - File.GetLastWriteTimeUtc(HashFile) > Lifetime;
}
