namespace TurboManager.Panel.Auth;

public static class AuthSchemes
{
    /// <summary>Full session after password + second factor.</summary>
    public const string Session = "ts-session";

    /// <summary>Password verified, waiting for the second factor (short-lived).</summary>
    public const string MfaPending = "ts-mfa";

    /// <summary>Setup token verified; allows creating the owner account (short-lived).</summary>
    public const string Setup = "ts-setup";

    /// <summary>API tokens (Authorization: Bearer ts_...), for scripts and CI.</summary>
    public const string Token = "ts-token";

    public const string UserIdClaim = "ts:uid";

    public const string StampClaim = "ts:stamp";

    public const string SessionIdClaim = "ts:sid";

    public const string TokenIdClaim = "ts:tid";
}
