using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace TurboManager.Panel.Cloudflare;

public sealed class CloudflareApiException(HttpStatusCode status, IReadOnlyList<CloudflareError> errors)
    : Exception(errors.Count > 0 ? string.Join("; ", errors.Select(e => $"{e.Message} ({e.Code})")) : $"Cloudflare returned {(int)status}.")
{
    public HttpStatusCode Status { get; } = status;

    public IReadOnlyList<CloudflareError> Errors { get; } = errors;

    /// <summary>Authentication or permission problems with the token.</summary>
    public bool IsPermissionError => Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized || Errors.Any(e => e.Code is 9109 or 10000 or 10001);
}

public sealed record CloudflareError(int Code, string Message);

public sealed record CloudflareTokenStatus(string Id, string Status, [property: JsonPropertyName("expires_on")] DateTimeOffset? ExpiresOn);

public sealed record CloudflareZoneInfo(string Id, string Name, string Status, CloudflarePlan? Plan);

public sealed record CloudflarePlan(string? Name);

public sealed record CloudflareDnsRecord(
    string? Id,
    string Type,
    string Name,
    string Content,
    bool Proxied,
    int Ttl = 1,
    string? Comment = null);

public sealed record CloudflareZoneSetting(string Id, JsonElement Value, bool Editable);

public sealed record CloudflareIpRanges([property: JsonPropertyName("ipv4_cidrs")] List<string> Ipv4Cidrs, [property: JsonPropertyName("ipv6_cidrs")] List<string> Ipv6Cidrs);

public sealed record CloudflareOriginCertificate(string Id, string Certificate, [property: JsonPropertyName("expires_on")] string? ExpiresOn);

/// <summary>Typed client for the Cloudflare v4 API (only the endpoints the panel uses).</summary>
public sealed class CloudflareClient(HttpClient http, IOptions<PanelOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string BaseUrl => options.Value.CloudflareApiBase.TrimEnd('/');

    public Task<CloudflareTokenStatus> VerifyTokenAsync(string token, CancellationToken cancellationToken) =>
        SendAsync<CloudflareTokenStatus>(token, HttpMethod.Get, "/user/tokens/verify", null, cancellationToken);

    public Task<List<CloudflareZoneInfo>> ListZonesAsync(string token, CancellationToken cancellationToken) =>
        SendAsync<List<CloudflareZoneInfo>>(token, HttpMethod.Get, "/zones?per_page=50", null, cancellationToken);

    public Task<List<CloudflareDnsRecord>> ListDnsRecordsAsync(string token, string zoneId, string name, CancellationToken cancellationToken) =>
        SendAsync<List<CloudflareDnsRecord>>(token, HttpMethod.Get, $"/zones/{zoneId}/dns_records?per_page=100&name={Uri.EscapeDataString(name)}", null, cancellationToken);

    public Task<CloudflareDnsRecord> CreateDnsRecordAsync(string token, string zoneId, CloudflareDnsRecord record, CancellationToken cancellationToken) =>
        SendAsync<CloudflareDnsRecord>(token, HttpMethod.Post, $"/zones/{zoneId}/dns_records", record with { Id = null }, cancellationToken);

    public Task<CloudflareDnsRecord> UpdateDnsRecordAsync(string token, string zoneId, CloudflareDnsRecord record, CancellationToken cancellationToken) =>
        SendAsync<CloudflareDnsRecord>(token, HttpMethod.Put, $"/zones/{zoneId}/dns_records/{record.Id}", record with { Id = null }, cancellationToken);

    public Task<JsonElement> DeleteDnsRecordAsync(string token, string zoneId, string recordId, CancellationToken cancellationToken) =>
        SendAsync<JsonElement>(token, HttpMethod.Delete, $"/zones/{zoneId}/dns_records/{recordId}", null, cancellationToken);

    public Task<List<CloudflareZoneSetting>> GetZoneSettingsAsync(string token, string zoneId, CancellationToken cancellationToken) =>
        SendAsync<List<CloudflareZoneSetting>>(token, HttpMethod.Get, $"/zones/{zoneId}/settings", null, cancellationToken);

    public Task<CloudflareZoneSetting> GetZoneSettingAsync(string token, string zoneId, string setting, CancellationToken cancellationToken) =>
        SendAsync<CloudflareZoneSetting>(token, HttpMethod.Get, $"/zones/{zoneId}/settings/{setting}", null, cancellationToken);

    public Task<CloudflareZoneSetting> UpdateZoneSettingAsync(string token, string zoneId, string setting, object value, CancellationToken cancellationToken) =>
        SendAsync<CloudflareZoneSetting>(token, HttpMethod.Patch, $"/zones/{zoneId}/settings/{setting}", new { value }, cancellationToken);

    /// <summary>Purges everything, or only the given hostnames/URLs.</summary>
    public Task<JsonElement> PurgeCacheAsync(string token, string zoneId, IReadOnlyList<string>? hosts, IReadOnlyList<string>? files, CancellationToken cancellationToken) =>
        SendAsync<JsonElement>(token, HttpMethod.Post, $"/zones/{zoneId}/purge_cache",
            hosts is { Count: > 0 } ? new { hosts } : files is { Count: > 0 } ? new { files } : new { purge_everything = true }, cancellationToken);

    public Task<List<CloudflareOriginCertificate>> ListOriginCertificatesAsync(string token, string zoneId, CancellationToken cancellationToken) =>
        SendAsync<List<CloudflareOriginCertificate>>(token, HttpMethod.Get, $"/certificates?zone_id={zoneId}", null, cancellationToken);

    public Task<CloudflareOriginCertificate> CreateOriginCertificateAsync(string token, string csr, IReadOnlyList<string> hostnames, CancellationToken cancellationToken) =>
        SendAsync<CloudflareOriginCertificate>(token, HttpMethod.Post, "/certificates",
            new { csr, hostnames, request_type = "origin-ecc", requested_validity = 5475 }, cancellationToken);

    /// <summary>Cloudflare's published edge ranges (public, no token).</summary>
    public Task<CloudflareIpRanges> GetIpRangesAsync(CancellationToken cancellationToken) =>
        SendAsync<CloudflareIpRanges>(null, HttpMethod.Get, "/ips", null, cancellationToken);

    private async Task<T> SendAsync<T>(string? token, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        Envelope<T>? envelope;

        try
        {
            envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            throw new CloudflareApiException(response.StatusCode, [new CloudflareError(0, $"Unexpected response ({(int)response.StatusCode}).")]);
        }

        if (envelope is null || !envelope.Success || !response.IsSuccessStatusCode)
        {
            throw new CloudflareApiException(response.StatusCode, envelope?.Errors ?? []);
        }

        return envelope.Result!;
    }

    private sealed record Envelope<T>(bool Success, List<CloudflareError>? Errors, T? Result);
}
