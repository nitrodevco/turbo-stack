using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TurboManager.Core;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Auth;

public sealed class PasskeyException(string message) : Exception(message);

/// <summary>
/// Passkeys (WebAuthn). A passkey is bound to the domain it was made on (the relying party is the panel's host name), so
/// it needs the panel's own domain: browsers don't allow passkeys for an IP address. The options of each ceremony are
/// kept server-side for five minutes under a random id; the browser only gets the id back with its response.
/// </summary>
public sealed class PasskeyService(PanelDbContext db, IMemoryCache cache, TimeProvider time)
{
    private static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);

    private sealed record Ceremony(string RpId, int? UserId, CredentialCreateOptions? Create, AssertionOptions? Assert);

    /// <summary>The domain passkeys are made for here, or null when the panel is reached by IP address.</summary>
    public static string? RelyingParty(HttpContext http)
    {
        var host = http.Request.Host.Host;

        return host.Length == 0 || IPAddress.TryParse(host.Trim('[', ']'), out _) ? null : host.ToLowerInvariant();
    }

    public (string Id, JsonNode Options) BeginRegistration(HttpContext http, PanelUser user, IReadOnlyList<Passkey> existing)
    {
        var fido = Create(http, out var rpId);
        user.PasskeyHandle ??= Base64Url(RandomNumberGenerator.GetBytes(32));

        var options = fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = FromBase64Url(user.PasskeyHandle), Name = user.Username, DisplayName = user.Username },
            ExcludeCredentials = [.. existing.Where(p => p.RpId == rpId).Select(p => new PublicKeyCredentialDescriptor(FromBase64Url(p.CredentialId)))],
            // A discoverable credential (it signs in without typing a username) that checks a PIN or biometric.
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        return (Remember(new Ceremony(rpId, user.Id, options, null)), JsonNode.Parse(options.ToJson())!);
    }

    public async Task<Passkey> FinishRegistrationAsync(HttpContext http, PanelUser user, string ceremonyId, JsonElement response, string name,
        CancellationToken cancellationToken)
    {
        var ceremony = Take(ceremonyId);

        if (ceremony?.Create is null || ceremony.UserId != user.Id)
        {
            throw new PasskeyException("This passkey request expired. Try again.");
        }

        var fido = Create(http, out var rpId);

        if (rpId != ceremony.RpId)
        {
            throw new PasskeyException("The panel's address changed during the request. Try again.");
        }

        RegisteredPublicKeyCredential credential;

        try
        {
            credential = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response.Deserialize<AuthenticatorAttestationRawResponse>()!,
                OriginalOptions = ceremony.Create,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    !await db.Passkeys.AnyAsync(p => p.CredentialId == Base64Url(args.CredentialId), token),
            }, cancellationToken);
        }
        catch (Exception e) when (e is Fido2VerificationException or JsonException or ArgumentException)
        {
            throw new PasskeyException($"The passkey couldn't be verified: {e.Message}");
        }

        var passkey = new Passkey
        {
            UserId = user.Id,
            Name = name,
            CredentialId = Base64Url(credential.Id),
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            RpId = rpId,
        };
        db.Passkeys.Add(passkey);

        return passkey;
    }

    /// <summary>Sign-in (any passkey for this domain: the browser offers the user's) or confirming it's you (only that user's).</summary>
    public async Task<(string Id, JsonNode Options)> BeginAssertionAsync(HttpContext http, PanelUser? user, CancellationToken cancellationToken)
    {
        var fido = Create(http, out var rpId);
        var allowed = user is null
            ? []
            : await db.Passkeys.Where(p => p.UserId == user.Id && p.RpId == rpId).Select(p => p.CredentialId).ToListAsync(cancellationToken);

        if (user is not null && allowed.Count == 0)
        {
            throw new PasskeyException("You have no passkey for this address yet.");
        }

        var options = fido.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [.. allowed.Select(id => new PublicKeyCredentialDescriptor(FromBase64Url(id)))],
            UserVerification = UserVerificationRequirement.Required,
        });

        return (Remember(new Ceremony(rpId, user?.Id, null, options)), JsonNode.Parse(options.ToJson())!);
    }

    /// <summary>The user the passkey belongs to, once its signature checks out (and the sign counter moved on).</summary>
    public async Task<PanelUser> FinishAssertionAsync(HttpContext http, string ceremonyId, JsonElement response, CancellationToken cancellationToken)
    {
        var ceremony = Take(ceremonyId);

        if (ceremony?.Assert is null)
        {
            throw new PasskeyException("This passkey request expired. Try again.");
        }

        var fido = Create(http, out var rpId);
        AuthenticatorAssertionRawResponse assertion;

        try
        {
            assertion = response.Deserialize<AuthenticatorAssertionRawResponse>()!;
        }
        catch (JsonException)
        {
            throw new PasskeyException("That isn't a passkey response.");
        }

        var credentialId = Base64Url(assertion.RawId);
        var passkey = await db.Passkeys.FirstOrDefaultAsync(p => p.CredentialId == credentialId && p.RpId == rpId, cancellationToken);
        var user = passkey is null ? null : await db.Users.Include(u => u.RecoveryCodes).FirstOrDefaultAsync(u => u.Id == passkey.UserId, cancellationToken);

        if (passkey is null || user is null || rpId != ceremony.RpId || (ceremony.UserId is { } expected && expected != user.Id))
        {
            throw new PasskeyException("This passkey isn't known here.");
        }

        try
        {
            var result = await fido.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertion,
                OriginalOptions = ceremony.Assert,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(user.PasskeyHandle is not null && args.UserHandle.AsSpan().SequenceEqual(FromBase64Url(user.PasskeyHandle))),
            }, cancellationToken);

            passkey.SignCount = result.SignCount;
        }
        catch (Exception e) when (e is Fido2VerificationException or ArgumentException)
        {
            throw new PasskeyException($"The passkey couldn't be verified: {e.Message}");
        }

        passkey.LastUsedAt = time.GetUtcNow();

        return user;
    }

    private static Fido2 Create(HttpContext http, out string rpId)
    {
        rpId = RelyingParty(http) ?? throw new PasskeyException("Passkeys need the panel's own domain (browsers don't allow them for an IP address).");

        return new Fido2(new Fido2Configuration
        {
            RPID = rpId,
            RPName = Branding.ProductName,
            // Exactly the address this request came to (nginx passes the host and the scheme).
            Origins = new HashSet<string> { $"{http.Request.Scheme}://{http.Request.Host}" },
            Timeout = (uint)CeremonyLifetime.TotalMilliseconds,
        });
    }

    private string Remember(Ceremony ceremony)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        cache.Set($"passkey:{id}", ceremony, CeremonyLifetime);

        return id;
    }

    /// <summary>Each ceremony is answered once.</summary>
    private Ceremony? Take(string id)
    {
        if (string.IsNullOrEmpty(id) || !cache.TryGetValue($"passkey:{id}", out Ceremony? ceremony))
        {
            return null;
        }

        cache.Remove($"passkey:{id}");

        return ceremony;
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }
}
