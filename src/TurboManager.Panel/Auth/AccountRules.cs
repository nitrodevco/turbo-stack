using System.Text.RegularExpressions;

namespace TurboManager.Panel.Auth;

public static partial class AccountRules
{
    public const int MinPasswordLength = 12;

    public const int MaxPasswordLength = 256;

    public static string NormalizeUsername(string? username) => (username ?? "").Trim().ToLowerInvariant();

    public static string? ValidateUsername(string username) =>
        UsernamePattern().IsMatch(username)
            ? null
            : "Username must be 3-32 characters: lowercase letters, digits, '.', '_' or '-', starting with a letter or digit.";

    public static string? ValidatePassword(string? password, string username)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return $"Password must be at least {MinPasswordLength} characters.";
        }

        if (password.Length > MaxPasswordLength)
        {
            return $"Password must be at most {MaxPasswordLength} characters.";
        }

        if (password.Contains(username, StringComparison.OrdinalIgnoreCase))
        {
            return "Password must not contain the username.";
        }

        return null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{2,31}$")]
    private static partial Regex UsernamePattern();
}
