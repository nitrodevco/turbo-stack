using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TurboManager.FakeCloudflare;

/// <summary>
/// In-memory stand-in for the Cloudflare v4 API endpoints the panel uses. Records every write so
/// tests can assert on them (GET /__fake/state). Tokens:
///   "test-token"       – every permission
///   "dns-only-token"   – zones + DNS, nothing else
///   anything else      – rejected
/// </summary>
public sealed class FakeCloudflareState
{
    public ConcurrentDictionary<string, JsonObject> Records { get; } = new();

    public ConcurrentQueue<JsonObject> Purges { get; } = new();

    public ConcurrentDictionary<string, JsonNode?> Settings { get; } = new(new Dictionary<string, JsonNode?>
    {
        ["ssl"] = "flexible",
        ["always_use_https"] = "off",
        ["min_tls_version"] = "1.0",
        ["websockets"] = "on",
        ["http3"] = "off",
        ["rocket_loader"] = "on",
        ["email_obfuscation"] = "on",
    });

    public ConcurrentQueue<string> OriginCertificates { get; } = new();

    public List<string> Ipv4Ranges { get; set; } = ["173.245.48.0/20", "103.21.244.0/22", "104.16.0.0/13"];

    public List<string> Ipv6Ranges { get; set; } = ["2400:cb00::/32"];

    public static readonly (string Id, string Name)[] Zones = [("zone-hotel", "hotel.test"), ("zone-example", "example.test")];

    public X509Certificate2 OriginCa { get; } = CreateCa();

    private static X509Certificate2 CreateCa()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Fake Cloudflare Origin CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
    }
}

public static class FakeCloudflareServer
{
    public static WebApplication Create(string[] args, string? urls = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Logging.ClearProviders();

        if (urls is not null)
        {
            builder.WebHost.UseUrls(urls);
        }

        var state = new FakeCloudflareState();
        builder.Services.AddSingleton(state);

        var app = builder.Build();
        var api = app.MapGroup("/client/v4");

        api.MapGet("/ips", () => Ok(new JsonObject
        {
            ["ipv4_cidrs"] = new JsonArray([.. state.Ipv4Ranges.Select(r => (JsonNode)r)]),
            ["ipv6_cidrs"] = new JsonArray([.. state.Ipv6Ranges.Select(r => (JsonNode)r)]),
        }));

        api.MapGet("/user/tokens/verify", (HttpRequest request) =>
            Token(request) is null ? Unauthorized() : Ok(new JsonObject { ["id"] = "token-id", ["status"] = "active", ["expires_on"] = null }));

        api.MapGet("/zones", (HttpRequest request) =>
            Token(request) is null
                ? Unauthorized()
                : Ok(new JsonArray([.. FakeCloudflareState.Zones.Select(z => (JsonNode)new JsonObject
                {
                    ["id"] = z.Id,
                    ["name"] = z.Name,
                    ["status"] = "active",
                    ["plan"] = new JsonObject { ["name"] = "Free Website" },
                })])));

        api.MapGet("/zones/{zone}/dns_records", (string zone, string? name, HttpRequest request) =>
            Token(request) is null
                ? Unauthorized()
                : Ok(new JsonArray([.. state.Records.Values
                    .Where(r => (string)r["zone"]! == zone && (name is null || (string)r["name"]! == name))
                    .Select(r => r.DeepClone())])));

        api.MapPost("/zones/{zone}/dns_records", async (string zone, HttpRequest request) =>
        {
            if (Token(request) is null)
            {
                return Unauthorized();
            }

            var record = (await JsonNode.ParseAsync(request.Body))!.AsObject();
            record["id"] = Guid.NewGuid().ToString("N");
            record["zone"] = zone;
            state.Records[(string)record["id"]!] = record;

            return Ok(record.DeepClone());
        });

        api.MapPut("/zones/{zone}/dns_records/{id}", async (string zone, string id, HttpRequest request) =>
        {
            if (Token(request) is null)
            {
                return Unauthorized();
            }

            var record = (await JsonNode.ParseAsync(request.Body))!.AsObject();
            record["id"] = id;
            record["zone"] = zone;
            state.Records[id] = record;

            return Ok(record.DeepClone());
        });

        api.MapDelete("/zones/{zone}/dns_records/{id}", (string zone, string id, HttpRequest request) =>
        {
            if (Token(request) is null)
            {
                return Unauthorized();
            }

            state.Records.TryRemove(id, out _);

            return Ok(new JsonObject { ["id"] = id });
        });

        api.MapGet("/zones/{zone}/settings", (HttpRequest request) =>
            Token(request) is null
                ? Unauthorized()
                : Ok(new JsonArray([.. state.Settings.Select(s => (JsonNode)Setting(s.Key, s.Value))])));

        api.MapGet("/zones/{zone}/settings/{name}", (string name, HttpRequest request) =>
            Token(request) is null ? Unauthorized() : Ok(Setting(name, state.Settings.GetValueOrDefault(name))));

        api.MapMethods("/zones/{zone}/settings/{name}", ["PATCH"], async (string name, HttpRequest request) =>
        {
            if (Token(request) is not "full")
            {
                return Forbidden();
            }

            var body = await JsonNode.ParseAsync(request.Body);
            state.Settings[name] = body!["value"]?.DeepClone();

            return Ok(Setting(name, state.Settings[name]));
        });

        api.MapPost("/zones/{zone}/purge_cache", async (string zone, HttpRequest request) =>
        {
            if (Token(request) is not "full")
            {
                return Forbidden();
            }

            var body = (await JsonNode.ParseAsync(request.Body))!.AsObject();
            body["zone"] = zone;
            state.Purges.Enqueue(body);

            return Ok(new JsonObject { ["id"] = zone });
        });

        api.MapGet("/certificates", (HttpRequest request) => Token(request) is not "full" ? Forbidden() : Ok(new JsonArray()));

        api.MapPost("/certificates", async (HttpRequest request) =>
        {
            if (Token(request) is not "full")
            {
                return Forbidden();
            }

            var body = (await JsonNode.ParseAsync(request.Body))!.AsObject();
            var csr = CertificateRequest.LoadSigningRequestPem((string)body["csr"]!, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            var san = new SubjectAlternativeNameBuilder();

            foreach (var hostname in body["hostnames"]!.AsArray())
            {
                san.AddDnsName((string)hostname!);
            }

            csr.CertificateExtensions.Clear();
            csr.CertificateExtensions.Add(san.Build());
            var serial = RandomNumberGenerator.GetBytes(12);
            using var issued = csr.Create(state.OriginCa, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(15), serial);
            var pem = issued.ExportCertificatePem();
            state.OriginCertificates.Enqueue(pem);

            return Ok(new JsonObject { ["id"] = Convert.ToHexStringLower(serial), ["certificate"] = pem, ["expires_on"] = issued.NotAfter.ToString("u") });
        });

        // Test hooks.
        // Not Cloudflare: a webhook receiver for the panel's alert tests (POSTs recorded, listed at GET).
        var webhooks = new List<System.Text.Json.Nodes.JsonNode?>();
        app.MapPost("/__fake/webhook", async (HttpRequest request) =>
        {
            var body = await System.Text.Json.Nodes.JsonNode.ParseAsync(request.Body);

            lock (webhooks)
            {
                webhooks.Add(body);
            }

            return Results.NoContent();
        });
        app.MapGet("/__fake/webhooks", () =>
        {
            lock (webhooks)
            {
                return Results.Json(webhooks.Select(w => w?.DeepClone()).ToList());
            }
        });

        app.MapGet("/__fake/state", () => Results.Json(new
        {
            records = state.Records.Values.Select(r => r.DeepClone()),
            purges = state.Purges.Select(p => p.DeepClone()),
            settings = state.Settings,
            originCertificates = state.OriginCertificates.Count,
        }));

        return app;
    }

    /// <summary>"full", "dns-only" or null (rejected).</summary>
    private static string? Token(HttpRequest request) => request.Headers.Authorization.ToString() switch
    {
        "Bearer test-token" => "full",
        "Bearer dns-only-token" => "dns-only",
        _ => null,
    };

    private static JsonObject Setting(string name, JsonNode? value) => new() { ["id"] = name, ["value"] = value?.DeepClone(), ["editable"] = true };

    private static IResult Ok(JsonNode result) =>
        Results.Json(new JsonObject { ["success"] = true, ["errors"] = new JsonArray(), ["result"] = result });

    private static IResult Unauthorized() => Error(401, 1000, "Invalid API Token");

    private static IResult Forbidden() => Error(403, 10000, "Authentication error");

    private static IResult Error(int status, int code, string message) => Results.Json(new JsonObject
    {
        ["success"] = false,
        ["errors"] = new JsonArray(new JsonObject { ["code"] = code, ["message"] = message }),
        ["result"] = null,
    }, statusCode: status);
}
