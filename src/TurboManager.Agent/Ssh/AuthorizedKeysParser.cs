using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Ssh;

public sealed record ParsedKey(string Options, string Type, string Blob, string Comment, string Fingerprint)
{
    public string ToLine()
    {
        var parts = new List<string>();

        if (Options.Length > 0)
        {
            parts.Add(Options);
        }

        parts.Add(Type);
        parts.Add(Blob);

        if (Comment.Length > 0)
        {
            parts.Add(Comment);
        }

        return string.Join(' ', parts);
    }

    public AuthorizedKey ToContract() => new() { Type = Type, Fingerprint = Fingerprint, Comment = Comment, Options = Options };
}

/// <summary>Parses and validates authorized_keys lines: [options] type base64-blob [comment].</summary>
public static class AuthorizedKeysParser
{
    public static readonly HashSet<string> KeyTypes =
    [
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com",
    ];

    public static List<ParsedKey> ParseFile(string content) =>
        content.Split('\n').Select(l => TryParse(l, out var key, out _) ? key : null).OfType<ParsedKey>().ToList();

    public static bool TryParse(string line, out ParsedKey key, out string error)
    {
        key = null!;
        error = "";
        line = line.Trim();

        if (line.Length == 0 || line.StartsWith('#'))
        {
            error = "Empty line.";

            return false;
        }

        if (line.Length > 16_384 || line.Contains('\0'))
        {
            error = "Key is too long or contains invalid characters.";

            return false;
        }

        var tokens = Tokenize(line);
        var typeIndex = tokens.FindIndex(KeyTypes.Contains);

        if (typeIndex < 0 || typeIndex + 1 >= tokens.Count)
        {
            error = $"Not a supported public key. Expected one of: {string.Join(", ", KeyTypes)}.";

            return false;
        }

        var type = tokens[typeIndex];
        var blob = tokens[typeIndex + 1];
        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(blob);
        }
        catch (FormatException)
        {
            error = "The key data is not valid base64.";

            return false;
        }

        // The blob starts with a length-prefixed copy of the key type; it must match the declared type.
        if (EmbeddedType(bytes) != type)
        {
            error = "The key data doesn't match its type.";

            return false;
        }

        var options = string.Join(' ', tokens.Take(typeIndex));

        if (options.Contains('\n'))
        {
            error = "Invalid key options.";

            return false;
        }

        key = new ParsedKey(options, type, blob, string.Join(' ', tokens.Skip(typeIndex + 2)), Fingerprint(bytes));

        return true;
    }

    private static string? EmbeddedType(byte[] blob)
    {
        if (blob.Length < 4)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(blob);

        return length > 0 && length <= blob.Length - 4 ? Encoding.ASCII.GetString(blob, 4, length) : null;
    }

    /// <summary>OpenSSH-style fingerprint: "SHA256:" + unpadded base64 of the blob's SHA-256.</summary>
    public static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    /// <summary>Splits on spaces outside double quotes (options like command="a b" stay one token).</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
