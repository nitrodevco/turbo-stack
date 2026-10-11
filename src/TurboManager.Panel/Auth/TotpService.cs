using Microsoft.AspNetCore.DataProtection;
using OtpNet;
using TurboManager.Core;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

public sealed record TotpEnrollment(string Secret, string OtpAuthUri);

public sealed class TotpService(IDataProtectionProvider dataProtection, TimeProvider time)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("TurboManager.Totp.v1");

    public TotpEnrollment BeginEnrollment(PanelUser user, string issuerHost)
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        var base32 = Base32Encoding.ToString(secret);

        user.TotpSecretProtected = _protector.Protect(base32);
        user.TotpEnabled = false;
        user.LastTotpStep = 0;

        var issuer = string.IsNullOrWhiteSpace(issuerHost) ? Branding.ProductName : $"{Branding.ProductName} ({issuerHost})";
        var uri = new OtpUri(OtpType.Totp, base32, user.Username, issuer).ToString();

        return new TotpEnrollment(base32, uri);
    }

    /// <summary>Verifies a 6-digit code, rejecting reuse of an already accepted time step.</summary>
    public bool Verify(PanelUser user, string? code)
    {
        if (user.TotpSecretProtected is null || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var normalized = new string(code.Where(char.IsDigit).ToArray());

        if (normalized.Length != 6)
        {
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(_protector.Unprotect(user.TotpSecretProtected)));
        var now = time.GetUtcNow().UtcDateTime;

        if (!totp.VerifyTotp(now, normalized, out var step, VerificationWindow.RfcSpecifiedNetworkDelay) || step <= user.LastTotpStep)
        {
            return false;
        }

        user.LastTotpStep = step;

        return true;
    }
}
