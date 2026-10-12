using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace TurboManager.Panel.Deploy;

/// <summary>
/// GitHub's SSH host keys, pinned for every clone. Fetched from GitHub's published metadata
/// (api.github.com/meta) and cached; if that's unreachable the long-standing Ed25519 key is used.
/// </summary>
public sealed class GitHubHostKeys(IHttpClientFactory httpClients, IMemoryCache cache, IOptions<PanelOptions> options, ILogger<GitHubHostKeys> logger)
{
    /// <summary>github.com's Ed25519 host key (published in GitHub's docs since 2022).</summary>
    public const string FallbackEd25519 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";

    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken cancellationToken)
    {
        if (options.Value.GitHubKnownHosts.Length > 0)
        {
            return options.Value.GitHubKnownHosts;
        }

        if (cache.TryGetValue("github-host-keys", out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        IReadOnlyList<string> keys = [FallbackEd25519];

        try
        {
            var client = httpClients.CreateClient("github");
            using var meta = JsonDocument.Parse(await client.GetStringAsync($"{options.Value.GitHubApiBase.TrimEnd('/')}/meta", cancellationToken));
            var published = meta.RootElement.GetProperty("ssh_keys").EnumerateArray().Select(k => k.GetString()!).Where(k => k.Split(' ').Length == 2).ToList();

            if (published.Count > 0)
            {
                keys = published;
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning(e, "Could not fetch GitHub's SSH host keys; using the pinned Ed25519 key");
        }

        cache.Set("github-host-keys", keys, TimeSpan.FromHours(12));

        return keys;
    }
}
