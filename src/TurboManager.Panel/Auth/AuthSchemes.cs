namespace TurboManager.Panel.Auth;

public static class AuthSchemes
{
    /// <summary>Full session after password + second factor.</summary>
    public const string Session = "tm-session";

    /// <summary>Password verified, waiting for the second factor (short-lived).</summary>
    public const string MfaPending = "tm-mfa";

    /// <summary>Setup token verified; allows creating the owner account (short-lived).</summary>
    public const string Setup = "tm-setup";

    /// <summary>API tokens (Authorization: Bearer tm_...), for scripts and CI.</summary>
    public const string Token = "tm-token";

    public const string UserIdClaim = "tm:uid";

    public const string StampClaim = "tm:stamp";

    public const string SessionIdClaim = "tm:sid";

    public const string TokenIdClaim = "tm:tid";
}
