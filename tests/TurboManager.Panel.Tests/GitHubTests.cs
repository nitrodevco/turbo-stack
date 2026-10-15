using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TurboManager.Panel.Deploy;

namespace TurboManager.Panel.Tests;

public sealed class GitHubTests : IAsyncLifetime
{
    private const string Token = "ghp_testtoken";

    private readonly PanelFactory _panel = new();
    private readonly FakeGitHubApi _github = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _panel.Settings["Panel:GitHubKnownHosts:0"] = "ssh-ed25519 AAAATESTKEY";
        _panel.ConfigureTestServices = services => services.AddHttpClient<GitHubClient>().ConfigurePrimaryHttpMessageHandler(() => _github);
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(_client);
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private Task<HttpResponseMessage> ConnectAsync(string token = Token) => _client.PutAsJsonAsync("/api/github/token", new { token });

    private async Task<JsonElement> AddRepositoryAsync(bool webhook = true)
    {
        var response = await _client.PostAsJsonAsync("/api/repositories", new
        {
            name = "turbo",
            systemUser = "hotel",
            repository = "billsonnn/turbo-cloud",
            branch = "main",
            @private = true,
            submodules = true,
            privateSubmodules = new[] { "nitrodevco/turbo-admin" },
            setUpOnGitHub = true,
            webhook,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Connecting_lists_repositories_and_branches()
    {
        Assert.False((await _client.GetFromJsonAsync<JsonElement>("/api/github")).GetProperty("connected").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await _client.GetAsync("/api/github/repositories")).StatusCode);

        var account = await (await ConnectAsync()).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("billsonnn", account.GetProperty("login").GetString());
        Assert.DoesNotContain(Token, account.GetRawText());

        var repositories = await _client.GetFromJsonAsync<JsonElement[]>("/api/github/repositories");
        var cloud = Assert.Single(repositories!, r => r.GetProperty("fullName").GetString() == "billsonnn/turbo-cloud");
        Assert.True(cloud.GetProperty("private").GetBoolean());
        Assert.True(cloud.GetProperty("admin").GetBoolean());
        Assert.DoesNotContain(repositories!, r => r.GetProperty("fullName").GetString() == "billsonnn/old-archived");

        Assert.Equal(["main", "dev"], await _client.GetFromJsonAsync<string[]>("/api/github/repositories/billsonnn/turbo-cloud/branches") ?? []);
    }

    [Fact]
    public async Task Bad_tokens_are_refused()
    {
        _github.Scopes = "read:user";
        Assert.Equal(HttpStatusCode.Conflict, (await ConnectAsync()).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await ConnectAsync("ghp_revoked")).StatusCode);
        Assert.False((await _client.GetFromJsonAsync<JsonElement>("/api/github")).GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Setting_up_on_github_needs_a_connected_account()
    {
        var response = await _client.PostAsJsonAsync("/api/repositories", new
        {
            name = "turbo", systemUser = "hotel", repository = "billsonnn/turbo-cloud", branch = "main", @private = true, setUpOnGitHub = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _client.GetFromJsonAsync<JsonElement[]>("/api/repositories") ?? []);
    }

    [Fact]
    public async Task Adding_a_repository_installs_its_deploy_keys_and_webhook()
    {
        (await ConnectAsync()).EnsureSuccessStatusCode();

        var repository = await AddRepositoryAsync();

        Assert.All(repository.GetProperty("gitHubSetup").EnumerateArray(), s => Assert.Equal("ok", s.GetProperty("status").GetString()));
        Assert.True(repository.GetProperty("deployKeyInstalledOnGitHub").GetBoolean());
        Assert.True(repository.GetProperty("webhookInstalledOnGitHub").GetBoolean());
        Assert.True(repository.GetProperty("submoduleKeys")[0].GetProperty("installedOnGitHub").GetBoolean());

        var key = Assert.Single(_github.Keys["billsonnn/turbo-cloud"]);
        Assert.Equal("ssh-ed25519 AAAAFAKEturbo", key.Key);
        Assert.Single(_github.Keys["nitrodevco/turbo-admin"]);

        var hook = Assert.Single(_github.Hooks["billsonnn/turbo-cloud"]);
        var id = repository.GetProperty("id").GetInt32();
        var webhook = await _client.GetFromJsonAsync<JsonElement>($"/api/repositories/{id}/webhook");
        Assert.Equal(webhook.GetProperty("url").GetString(), hook.Url);
        Assert.Equal(webhook.GetProperty("secret").GetString(), hook.Secret);
        Assert.Equal(["push"], hook.Events);

        // Running setup again reuses what's there instead of adding duplicates.
        (await _client.PostAsJsonAsync($"/api/repositories/{id}/github", new { webhook = true })).EnsureSuccessStatusCode();
        Assert.Single(_github.Keys["billsonnn/turbo-cloud"]);
        Assert.Single(_github.Hooks["billsonnn/turbo-cloud"]);
    }

    [Fact]
    public async Task Rotating_the_secret_updates_the_webhook_and_removing_cleans_up()
    {
        (await ConnectAsync()).EnsureSuccessStatusCode();
        var id = (await AddRepositoryAsync()).GetProperty("id").GetInt32();

        var rotated = await (await _client.PostAsync($"/api/repositories/{id}/webhook/rotate", null)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", rotated.GetProperty("github").GetProperty("status").GetString());
        Assert.Equal(rotated.GetProperty("secret").GetString(), Assert.Single(_github.Hooks["billsonnn/turbo-cloud"]).Secret);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/repositories/{id}")).StatusCode);
        Assert.Empty(_github.Keys["billsonnn/turbo-cloud"]);
        Assert.Empty(_github.Keys["nitrodevco/turbo-admin"]);
        Assert.Empty(_github.Hooks["billsonnn/turbo-cloud"]);
    }

    [Fact]
    public async Task Failures_are_reported_per_step_and_the_repository_is_kept()
    {
        (await ConnectAsync()).EnsureSuccessStatusCode();
        _github.NoAdmin.Add("nitrodevco/turbo-admin");

        var repository = await AddRepositoryAsync(webhook: false);

        var steps = repository.GetProperty("gitHubSetup").EnumerateArray().ToList();
        Assert.Equal(["ok", "failed"], steps.Select(s => s.GetProperty("status").GetString()));
        Assert.Contains("admin rights", steps[1].GetProperty("detail").GetString());
        Assert.False(repository.GetProperty("webhookInstalledOnGitHub").GetBoolean());
        Assert.False(repository.GetProperty("submoduleKeys")[0].GetProperty("installedOnGitHub").GetBoolean());
        Assert.Single(await _client.GetFromJsonAsync<JsonElement[]>("/api/repositories") ?? []);
    }

    /// <summary>Just enough of api.github.com: the user, repositories, branches, deploy keys and hooks.</summary>
    private sealed class FakeGitHubApi : HttpMessageHandler
    {
        private long _nextId = 100;

        public string Scopes { get; set; } = "repo, admin:repo_hook";

        public HashSet<string> NoAdmin { get; } = [];

        public Dictionary<string, List<(long Id, string Key)>> Keys { get; } = [];

        public Dictionary<string, List<FakeHook>> Hooks { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != Token)
            {
                return Json(HttpStatusCode.Unauthorized, new { message = "Bad credentials" });
            }

            var path = request.RequestUri!.AbsolutePath;
            // Only the first page of a list has items.
            var page = System.Text.RegularExpressions.Regex.IsMatch(request.RequestUri.Query, "[?&]page=1(&|$)");
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            var parts = path.Trim('/').Split('/');

            if (path == "/user")
            {
                var response = Json(HttpStatusCode.OK, new { login = "billsonnn", name = "Bill", avatar_url = "https://avatars.example/b" });
                response.Headers.Add("X-OAuth-Scopes", Scopes);

                return response;
            }

            if (path == "/user/repos")
            {
                return Json(HttpStatusCode.OK, !page ? [] : new object[]
                {
                    Repo("billsonnn", "turbo-cloud", true, false), Repo("nitrodevco", "turbo-admin", true, false), Repo("billsonnn", "old-archived", false, true),
                });
            }

            if (parts is ["repos", var owner, var name, ..])
            {
                var full = $"{owner}/{name}";

                if (NoAdmin.Contains(full) && parts.Length > 3 && parts[3] is "keys" or "hooks")
                {
                    return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
                }

                var keys = Keys.TryGetValue(full, out var k) ? k : Keys[full] = [];
                var hooks = Hooks.TryGetValue(full, out var h) ? h : Hooks[full] = [];

                switch (parts[3..])
                {
                    case ["branches"]:
                        return Json(HttpStatusCode.OK, page ? new[] { new { name = "main" }, new { name = "dev" } } : []);
                    case ["keys"] when request.Method == HttpMethod.Get:
                        return Json(HttpStatusCode.OK, page ? keys.Select(x => new { id = x.Id, key = x.Key, title = "t" }) : []);
                    case ["keys"]:
                        var key = (_nextId++, body!["key"]!.GetValue<string>());
                        Assert.True(body["read_only"]!.GetValue<bool>());
                        keys.Add(key);

                        return Json(HttpStatusCode.Created, new { id = key.Item1, key = key.Item2, title = body["title"]!.GetValue<string>() });
                    case ["keys", var keyId] when request.Method == HttpMethod.Delete:
                        keys.RemoveAll(x => x.Id == long.Parse(keyId));

                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case ["hooks"] when request.Method == HttpMethod.Get:
                        return Json(HttpStatusCode.OK, page ? hooks.Select(HookJson) : []);
                    case ["hooks"]:
                        var hook = new FakeHook(_nextId++);
                        hook.Apply(body!);
                        hooks.Add(hook);

                        return Json(HttpStatusCode.Created, HookJson(hook));
                    case ["hooks", var hookId] when request.Method == HttpMethod.Patch:
                        var existing = hooks.Single(x => x.Id == long.Parse(hookId));
                        existing.Apply(body!);

                        return Json(HttpStatusCode.OK, HookJson(existing));
                    case ["hooks", var hookId] when request.Method == HttpMethod.Delete:
                        hooks.RemoveAll(x => x.Id == long.Parse(hookId));

                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
            }

            return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
        }

        private static object Repo(string owner, string name, bool isPrivate, bool archived) => new
        {
            full_name = $"{owner}/{name}", name, owner = new { login = owner }, @private = isPrivate, description = (string?)null, default_branch = "main",
            pushed_at = "2026-10-01T00:00:00Z", archived, permissions = new { admin = true, push = true, pull = true },
        };

        private static object HookJson(FakeHook hook) => new { id = hook.Id, config = new { url = hook.Url, content_type = "json" } };

        private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private sealed class FakeHook(long id)
    {
        public long Id { get; } = id;

        public string? Url { get; private set; }

        public string? Secret { get; private set; }

        public string[] Events { get; private set; } = [];

        public void Apply(JsonNode body)
        {
            Url = body["config"]!["url"]!.GetValue<string>();
            Secret = body["config"]!["secret"]!.GetValue<string>();
            Assert.Equal("json", body["config"]!["content_type"]!.GetValue<string>());
            Events = body["events"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray();
        }
    }
}
