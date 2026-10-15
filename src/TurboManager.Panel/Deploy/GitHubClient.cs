using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace TurboManager.Panel.Deploy;

public sealed class GitHubApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The token is invalid, or lacks the permission (or admin rights on the repository) for this call.</summary>
    public bool IsPermissionError => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
}

public sealed record GitHubUser(string Login, string? Name, [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

public sealed record GitHubRepositoryOwner(string Login);

public sealed record GitHubRepositoryPermissions(bool Admin, bool Push, bool Pull);

public sealed record GitHubRepositoryInfo(
    [property: JsonPropertyName("full_name")] string FullName,
    string Name,
    GitHubRepositoryOwner Owner,
    bool Private,
    string? Description,
    [property: JsonPropertyName("default_branch")] string DefaultBranch,
    [property: JsonPropertyName("pushed_at")] DateTimeOffset? PushedAt,
    bool Archived,
    GitHubRepositoryPermissions? Permissions);

public sealed record GitHubBranch(string Name);

public sealed record GitHubDeployKey(long Id, string Key, string Title);

public sealed record GitHubHookConfig(string? Url);

public sealed record GitHubHook(long Id, GitHubHookConfig Config);

/// <summary>Typed client for the GitHub REST API (only the endpoints the panel uses).</summary>
public sealed class GitHubClient(HttpClient http, IOptions<PanelOptions> options)
{
    /// <summary>Pages of 100 read at most when listing (enough for 1000 repositories).</summary>
    private const int MaxPages = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string BaseUrl => options.Value.GitHubApiBase.TrimEnd('/');

    /// <summary>The token's user, and its scopes when it's a classic token (null for fine-grained tokens).</summary>
    public async Task<(GitHubUser User, string? Scopes)> GetUserAsync(string token, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, HttpMethod.Get, "/user", null, cancellationToken);
        var scopes = response.Headers.TryGetValues("X-OAuth-Scopes", out var values) ? string.Join(", ", values) : null;

        return ((await response.Content.ReadFromJsonAsync<GitHubUser>(Json, cancellationToken))!, scopes);
    }

    public Task<List<GitHubRepositoryInfo>> ListRepositoriesAsync(string token, CancellationToken cancellationToken) =>
        ListAsync<GitHubRepositoryInfo>(token, "/user/repos?affiliation=owner,collaborator,organization_member&sort=pushed", cancellationToken);

    public Task<List<GitHubBranch>> ListBranchesAsync(string token, string owner, string name, CancellationToken cancellationToken) =>
        ListAsync<GitHubBranch>(token, $"{RepoPath(owner, name)}/branches", cancellationToken);

    public Task<GitHubRepositoryInfo> GetRepositoryAsync(string token, string owner, string name, CancellationToken cancellationToken) =>
        GetAsync<GitHubRepositoryInfo>(token, HttpMethod.Get, RepoPath(owner, name), null, cancellationToken);

    public Task<List<GitHubDeployKey>> ListDeployKeysAsync(string token, string owner, string name, CancellationToken cancellationToken) =>
        ListAsync<GitHubDeployKey>(token, $"{RepoPath(owner, name)}/keys", cancellationToken);

    public Task<GitHubDeployKey> AddDeployKeyAsync(string token, string owner, string name, string title, string key, CancellationToken cancellationToken) =>
        GetAsync<GitHubDeployKey>(token, HttpMethod.Post, $"{RepoPath(owner, name)}/keys", new { title, key, read_only = true }, cancellationToken);

    public Task DeleteDeployKeyAsync(string token, string owner, string name, long id, CancellationToken cancellationToken) =>
        SendAndDisposeAsync(token, HttpMethod.Delete, $"{RepoPath(owner, name)}/keys/{id}", cancellationToken);

    public Task<List<GitHubHook>> ListHooksAsync(string token, string owner, string name, CancellationToken cancellationToken) =>
        ListAsync<GitHubHook>(token, $"{RepoPath(owner, name)}/hooks", cancellationToken);

    /// <summary>A push-only JSON webhook signed with <paramref name="secret"/>.</summary>
    public Task<GitHubHook> CreateHookAsync(string token, string owner, string name, string url, string secret, CancellationToken cancellationToken) =>
        GetAsync<GitHubHook>(token, HttpMethod.Post, $"{RepoPath(owner, name)}/hooks",
            new { name = "web", active = true, events = new[] { "push" }, config = HookConfig(url, secret) }, cancellationToken);

    public Task<GitHubHook> UpdateHookAsync(string token, string owner, string name, long id, string url, string secret, CancellationToken cancellationToken) =>
        GetAsync<GitHubHook>(token, HttpMethod.Patch, $"{RepoPath(owner, name)}/hooks/{id}",
            new { active = true, events = new[] { "push" }, config = HookConfig(url, secret) }, cancellationToken);

    public Task DeleteHookAsync(string token, string owner, string name, long id, CancellationToken cancellationToken) =>
        SendAndDisposeAsync(token, HttpMethod.Delete, $"{RepoPath(owner, name)}/hooks/{id}", cancellationToken);

    private static object HookConfig(string url, string secret) => new { url, secret, content_type = "json", insecure_ssl = "0" };

    private static string RepoPath(string owner, string name) => $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";

    private async Task<List<T>> ListAsync<T>(string token, string path, CancellationToken cancellationToken)
    {
        var all = new List<T>();
        var separator = path.Contains('?') ? '&' : '?';

        for (var page = 1; page <= MaxPages; page++)
        {
            var items = await GetAsync<List<T>>(token, HttpMethod.Get, $"{path}{separator}per_page=100&page={page}", null, cancellationToken);
            all.AddRange(items);

            if (items.Count < 100)
            {
                break;
            }
        }

        return all;
    }

    private async Task<T> GetAsync<T>(string token, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(token, method, path, body, cancellationToken);

        try
        {
            return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
        }
        catch (JsonException)
        {
            throw new GitHubApiException(response.StatusCode, $"Unexpected response from GitHub ({(int)response.StatusCode}).");
        }
    }

    private async Task SendAndDisposeAsync(string token, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(token, method, path, null, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        var response = await http.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw new GitHubApiException(response.StatusCode, await ErrorMessageAsync(response, cancellationToken));
        }
    }

    /// <summary>GitHub's message plus the first validation detail (e.g. "key is already in use").</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var message = error.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            var detail = error.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                ? errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var d) ? d.GetString() : null)
                    .FirstOrDefault(d => !string.IsNullOrEmpty(d))
                : null;

            return string.Join(": ", new[] { message, detail }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } text
                ? text
                : $"GitHub returned {(int)response.StatusCode}.";
        }
        catch (JsonException)
        {
            return $"GitHub returned {(int)response.StatusCode}.";
        }
    }
}
