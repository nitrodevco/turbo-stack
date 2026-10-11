namespace TurboManager.Panel.Auth;

public static class AuthSchemes
{
    /// <summary>Full session after password + second factor.</summary>
    public const string Session = "tm-session";

    /// <summary>Password verified, waiting for the second factor (short-lived).</summary>
    public const string MfaPending = "tm-mfa";

    /// <summary>Setup token verified; allows creating the owner account (short-lived).</summary>
    public const string Setup = "tm-setup";

    public const string UserIdClaim = "tm:uid";

    public const string StampClaim = "tm:stamp";
}
