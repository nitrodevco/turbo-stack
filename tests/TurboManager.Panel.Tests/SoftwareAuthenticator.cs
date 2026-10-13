using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TurboManager.Panel.Tests;

/// <summary>
/// A passkey authenticator in software (ES256, "none" attestation, user verified), producing what a browser's
/// navigator.credentials.create/get would send — so the server's real WebAuthn verification runs in tests.
/// </summary>
public sealed class SoftwareAuthenticator(string origin = "http://localhost") : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(16);
    private byte[] _userHandle = [];
    private uint _counter;

    public string CredentialId => B64(_credentialId);

    public void Dispose() => _key.Dispose();

    /// <summary>The PublicKeyCredential JSON for registration options (as the panel sends them).</summary>
    public JsonObject Create(JsonNode options)
    {
        var challenge = options["challenge"]!.GetValue<string>();
        var rpId = options["rp"]!["id"]!.GetValue<string>();
        _userHandle = FromB64(options["user"]!["id"]!.GetValue<string>());
        var clientData = ClientData("webauthn.create", challenge);

        var parameters = _key.ExportParameters(false);
        var cose = new CborWriter();
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);      // kty: EC2
        cose.WriteInt32(3); cose.WriteInt32(-7);     // alg: ES256
        cose.WriteInt32(-1); cose.WriteInt32(1);     // crv: P-256
        cose.WriteInt32(-2); cose.WriteByteString(parameters.Q.X!);
        cose.WriteInt32(-3); cose.WriteByteString(parameters.Q.Y!);
        cose.WriteEndMap();

        var authData = AuthData(rpId, flags: 0x45, // user present + verified + attested credential data
            [.. new byte[16], (byte)(_credentialId.Length >> 8), (byte)_credentialId.Length, .. _credentialId, .. cose.Encode()]);

        var attestation = new CborWriter();
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt"); attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt"); attestation.WriteStartMap(0); attestation.WriteEndMap();
        attestation.WriteTextString("authData"); attestation.WriteByteString(authData);
        attestation.WriteEndMap();

        return new JsonObject
        {
            ["id"] = CredentialId,
            ["rawId"] = CredentialId,
            ["type"] = "public-key",
            ["response"] = new JsonObject { ["attestationObject"] = B64(attestation.Encode()), ["clientDataJSON"] = B64(clientData) },
            ["clientExtensionResults"] = new JsonObject(),
        };
    }

    /// <summary>The PublicKeyCredential JSON for sign-in options.</summary>
    public JsonObject Get(JsonNode options, string? overrideOrigin = null)
    {
        var challenge = options["challenge"]!.GetValue<string>();
        var rpId = options["rpId"]!.GetValue<string>();
        var clientData = ClientData("webauthn.get", challenge, overrideOrigin);
        var authData = AuthData(rpId, flags: 0x05, []); // user present + verified
        var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new JsonObject
        {
            ["id"] = CredentialId,
            ["rawId"] = CredentialId,
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["authenticatorData"] = B64(authData),
                ["clientDataJSON"] = B64(clientData),
                ["signature"] = B64(signature),
                ["userHandle"] = B64(_userHandle),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };
    }

    private byte[] ClientData(string type, string challenge, string? overrideOrigin = null) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin = overrideOrigin ?? origin, crossOrigin = false }));

    private byte[] AuthData(string rpId, byte flags, byte[] attested)
    {
        _counter++;

        return [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), flags, (byte)(_counter >> 24), (byte)(_counter >> 16), (byte)(_counter >> 8), (byte)_counter, .. attested];
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }
}
