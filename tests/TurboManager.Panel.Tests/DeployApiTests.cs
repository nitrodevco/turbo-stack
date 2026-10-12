using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TurboManager.Panel.Deploy;

namespace TurboManager.Panel.Tests;

public sealed class DeployModelTests
{
    [Theory]
    [InlineData("billsonnn/turbo-cloud", "billsonnn", "turbo-cloud")]
    [InlineData("https://github.com/billsonnn/turbo-cloud", "billsonnn", "turbo-cloud")]
    [InlineData("https://github.com/billsonnn/turbo-cloud.git", "billsonnn", "turbo-cloud")]
    [InlineData("git@github.com:nitrodevco/nitro-next.git", "nitrodevco", "nitro-next")]
    [InlineData("ssh://git@github.com/nitrodevco/nitro-next.git", "nitrodevco", "nitro-next")]
    public void Repository_input_forms_are_understood(string input, string owner, string name) =>
        Assert.Equal((owner, name), DeployModels.ParseRepository(input));

    [Theory]
    [InlineData("https://gitlab.com/a/b")]
    [InlineData("just-a-name")]
    [InlineData("a/b/c")]
    public void Non_github_input_is_rejected(string input) => Assert.Null(DeployModels.ParseRepository(input));

    [Fact]
    public void Webhook_signatures_are_verified()
    {
        var body = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/main\"}");
        var good = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), body));

        Assert.True(DeployService.SignatureValid("secret", good, body));
        Assert.False(DeployService.SignatureValid("other", good, body));
        Assert.False(DeployService.SignatureValid("secret", null, body));
        Assert.False(DeployService.SignatureValid("secret", good.Replace("sha256=", "sha1="), body));
    }

    [Fact]
    public void Truncated_pushes_deploy_everything()
    {
        using var small = JsonDocument.Parse("""{"commits":[{"added":["a.txt"],"modified":["packages/nitro-imager/src/x.ts"],"removed":[]}]}""");
        using var large = JsonDocument.Parse("{\"commits\":[" + string.Join(',', Enumerable.Repeat("{}", 20)) + "]}");

        Assert.Equal(["a.txt", "packages/nitro-imager/src/x.ts"], DeployService.ChangedFiles(small.RootElement)!.Order());
        Assert.Null(DeployService.ChangedFiles(large.RootElement));
    }
}

public sealed class DeployApiTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _panel.Settings["Panel:DeployHealthTimeoutSeconds"] = "2";
        _panel.Settings["Panel:GitHubKnownHosts:0"] = "ssh-ed25519 AAAATESTKEY";
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(_client);
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> CreateRepositoryAsync(bool isPrivate = true) =>
        (await JsonAsync(await _client.PostAsJsonAsync("/api/repositories", new
        {
            name = "turbo",
            systemUser = "hotel",
            repository = "https://github.com/billsonnn/turbo-cloud",
            branch = "main",
            @private = isPrivate,
            submodules = true,
            privateSubmodules = new[] { "nitrodevco/turbo-admin" },
        }))).GetProperty("id").GetInt32();

    private async Task<int> CreateAppAsync(int repositoryId, string script = "dotnet publish", int? port = null, bool autoDeploy = true, string[]? deployPaths = null) =>
        (await JsonAsync(await _client.PostAsJsonAsync("/api/apps", new
        {
            name = "emulator",
            repositoryId,
            buildScript = script,
            runtimes = new[] { "dotnet" },
            sharedPaths = new[] { new { path = "assets", directory = true } },
            serviceCommand = "dotnet Turbo.Main.dll",
            servicePort = port,
            autoDeploy,
            deployPaths,
        }))).GetProperty("id").GetInt32();

    private async Task<JsonElement> WaitForJobAsync(long jobId)
    {
        for (var i = 0; i < 400; i++)
        {
            var job = await _client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");

            if (job.GetProperty("status").GetString() != "running")
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Job did not finish");
    }

    private async Task<JsonElement> DeployAsync(int appId) =>
        await WaitForJobAsync((await JsonAsync(await _client.PostAsync($"/api/apps/{appId}/deploy", null), HttpStatusCode.Accepted)).GetProperty("jobId").GetInt64());

    [Fact]
    public async Task Private_repository_gets_deploy_keys_for_itself_and_private_submodules()
    {
        var id = await CreateRepositoryAsync();
        var detail = await JsonAsync(await _client.GetAsync($"/api/repositories/{id}"));
        var repository = detail.GetProperty("repository");

        Assert.Contains("hotel", _agent.Deploy.Users);
        Assert.StartsWith("ssh-ed25519 AAAAFAKEturbo", repository.GetProperty("publicKey").GetString());
        Assert.Equal("nitrodevco/turbo-admin", repository.GetProperty("submoduleKeys")[0].GetProperty("repository").GetString());
        Assert.EndsWith($"/hooks/github/{id}", repository.GetProperty("webhookUrl").GetString());
        Assert.Equal("https://github.com/billsonnn/turbo-cloud/settings/keys", repository.GetProperty("deployKeysUrl").GetString());
    }

    [Fact]
    public async Task Deploy_syncs_builds_activates_and_restarts()
    {
        var appId = await CreateAppAsync(await CreateRepositoryAsync());
        await _client.PutAsJsonAsync($"/api/apps/{appId}/environment", new Dictionary<string, string> { ["TURBO__Turbo__HotelName"] = "Habbo" });

        var job = await DeployAsync(appId);

        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        var sync = Assert.Single(_agent.Deploy.Syncs);
        Assert.Equal("turbo", sync.Repository.KeyName);
        Assert.Equal("ssh-ed25519 AAAATESTKEY", Assert.Single(sync.KnownHosts));
        Assert.Equal("turbo-turbo-admin", sync.SubmoduleAccess[0].KeyName);
        Assert.Equal("Habbo", _agent.Deploy.Builds[0].Environment["TURBO__Turbo__HotelName"]);
        Assert.Equal("dotnet Turbo.Main.dll", _agent.Deploy.Services["emulator"].Command);
        Assert.Contains(_agent.Services.Controls, c => c.Name == "tm-app-emulator.service");

        var detail = await JsonAsync(await _client.GetAsync($"/api/apps/{appId}"));
        Assert.Equal("active", detail.GetProperty("releases")[0].GetProperty("status").GetString());
        Assert.Equal(_agent.Deploy.Current["emulator"], detail.GetProperty("app").GetProperty("currentRelease").GetString());
    }

    [Fact]
    public async Task Failed_build_leaves_the_live_release_alone()
    {
        var appId = await CreateAppAsync(await CreateRepositoryAsync(), script: "make fail");

        var job = await DeployAsync(appId);

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.False(_agent.Deploy.Current.ContainsKey("emulator"));
        var release = (await JsonAsync(await _client.GetAsync($"/api/apps/{appId}"))).GetProperty("releases")[0];
        Assert.Equal("failed", release.GetProperty("status").GetString());
        Assert.Contains("build failed", release.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unhealthy_service_is_rolled_back_to_the_previous_release()
    {
        // A port that accepts connections makes the first deploy healthy.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var appId = await CreateAppAsync(await CreateRepositoryAsync(), port: port);
        Assert.Equal("succeeded", (await DeployAsync(appId)).GetProperty("status").GetString());
        var first = _agent.Deploy.Current["emulator"];

        // Then nothing listens: the next deploy must roll back.
        listener.Stop();
        _agent.Deploy.Commit = "1234567890abcdef1234567890abcdef12345678";
        _panel.Time.Advance(TimeSpan.FromMinutes(1));
        var job = await DeployAsync(appId);

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Contains("rolled back", job.GetProperty("output").GetString());
        Assert.Equal(first, _agent.Deploy.Current["emulator"]);
    }

    [Fact]
    public async Task Rollback_activates_an_earlier_release()
    {
        var appId = await CreateAppAsync(await CreateRepositoryAsync());
        await DeployAsync(appId);
        var first = _agent.Deploy.Current["emulator"];
        _agent.Deploy.Commit = "1234567890abcdef1234567890abcdef12345678";
        _panel.Time.Advance(TimeSpan.FromMinutes(1));
        await DeployAsync(appId);
        Assert.NotEqual(first, _agent.Deploy.Current["emulator"]);

        var job = await WaitForJobAsync((await JsonAsync(await _client.PostAsync($"/api/apps/{appId}/releases/{first}/activate", null),
            HttpStatusCode.Accepted)).GetProperty("jobId").GetInt64());

        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        Assert.Equal(first, _agent.Deploy.Current["emulator"]);
        var releases = (await JsonAsync(await _client.GetAsync($"/api/apps/{appId}"))).GetProperty("releases");
        Assert.Equal("active", releases.EnumerateArray().First(r => r.GetProperty("name").GetString() == first).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Webhooks_need_a_valid_signature_and_ignore_replays_and_other_branches()
    {
        var repositoryId = await CreateRepositoryAsync();
        await CreateAppAsync(repositoryId);
        var secret = (await JsonAsync(await _client.GetAsync($"/api/repositories/{repositoryId}/webhook"))).GetProperty("secret").GetString()!;
        var hook = _panel.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendHookAsync(hook, repositoryId, "wrong-secret", "d1", "push", "refs/heads/main")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendHookAsync(hook, repositoryId, secret, "d2", "ping", null)).StatusCode);

        var other = await SendHookAsync(hook, repositoryId, secret, "d3", "push", "refs/heads/feature");
        Assert.Contains("ignored", (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());

        var push = await SendHookAsync(hook, repositoryId, secret, "d4", "push", "refs/heads/main");
        Assert.Equal(HttpStatusCode.Accepted, push.StatusCode);
        Assert.Contains("emulator: deploying", (await push.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await SendHookAsync(hook, repositoryId, secret, "d4", "push", "refs/heads/main")).StatusCode);
    }

    [Fact]
    public async Task Path_filters_skip_unrelated_pushes()
    {
        var repositoryId = await CreateRepositoryAsync();
        await CreateAppAsync(repositoryId, deployPaths: ["packages/nitro-imager"]);
        var secret = (await JsonAsync(await _client.GetAsync($"/api/repositories/{repositoryId}/webhook"))).GetProperty("secret").GetString()!;

        var response = await SendHookAsync(_panel.CreateClient(), repositoryId, secret, "d1", "push", "refs/heads/main", "packages/nitro-react/src/App.tsx");

        Assert.Contains("no changes under packages/nitro-imager", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Invalid_app_settings_are_rejected()
    {
        var repositoryId = await CreateRepositoryAsync();

        var response = await _client.PostAsJsonAsync("/api/apps", new { name = "web", repositoryId, staticOutput = "../../etc" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendHookAsync(HttpClient client, int repositoryId, string secret, string delivery, string eventName,
        string? gitRef, string changedFile = "src/Program.cs")
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            @ref = gitRef,
            commits = new[] { new { added = Array.Empty<string>(), modified = new[] { changedFile }, removed = Array.Empty<string>() } },
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hooks/github/{repositoryId}") { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-GitHub-Delivery", delivery);
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));

        return await client.SendAsync(request);
    }
}
