using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TurboManager.Contracts.Agent;

namespace TurboManager.Panel.Tests;

/// <summary>Answers every connection on a local port with "200 OK" (stands in for a deployed service's health check).</summary>
public sealed class HealthResponder : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    public HealthResponder(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    _ = await stream.ReadAtLeastAsync(new byte[4096], 1, throwOnEndOfStream: false, _stop.Token);
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), _stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or IOException)
                {
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

public sealed class StackApiTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;
    private readonly List<HealthResponder> _services = [];

    public async Task InitializeAsync()
    {
        _panel.Settings["Panel:DeployHealthTimeoutSeconds"] = "5";
        _panel.Settings["Panel:GitHubKnownHosts:0"] = "ssh-ed25519 AAAATESTKEY";
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(_client);
    }

    public async Task DisposeAsync()
    {
        _services.ForEach(s => s.Dispose());
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private static object Hotel(object? hosts = null, object? overrides = null) => new
    {
        name = "hotel",
        preset = "turbo-nitro",
        domain = "hotel.test",
        tlsMode = "SelfSigned",
        // An existing asset host instead of one on this server.
        hosts = hosts ?? new Dictionary<string, string> { ["assets"] = "" },
        settings = new Dictionary<string, string>
        {
            ["hotelName"] = "Test Hotel",
            ["ownerName"] = "bill",
            ["assets.url"] = "https://cdn.example.com",
            ["assets.badgeUrl"] = "https://cdn.example.com/badges/%badgename%.gif",
        },
        overrides,
    };

    private async Task<int> CreateAsync(object hotel)
    {
        var response = await _client.PostAsJsonAsync("/api/stacks", hotel);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> RunJobAsync(HttpResponseMessage started)
    {
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetInt64();

        for (var i = 0; i < 300; i++)
        {
            var job = await _client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}");

            if (job.GetProperty("status").GetString() != "running")
            {
                return job;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("job didn't finish");
    }

    private async Task<JsonElement> InstallAsync(int stackId)
    {
        // The emulator (WebSocket port) and the imager (/health) must answer after their deploys.
        if (_services.Count == 0)
        {
            _services.Add(new HealthResponder(9001));
            _services.Add(new HealthResponder(3030));
        }

        var job = await RunJobAsync(await _client.PostAsync($"/api/stacks/{stackId}/install", null));
        Assert.True(job.GetProperty("status").GetString() == "succeeded", job.GetProperty("output").GetString());

        return job;
    }

    private async Task<Dictionary<string, string>> EnvironmentAsync(string app)
    {
        var apps = await _client.GetFromJsonAsync<JsonElement[]>("/api/apps");
        var id = apps!.Single(a => a.GetProperty("name").GetString() == app).GetProperty("id").GetInt32();

        return (await _client.GetFromJsonAsync<Dictionary<string, string>>($"/api/apps/{id}/environment"))!;
    }

    [Fact]
    public async Task Presets_list_the_hotel_roles()
    {
        var presets = await _client.GetFromJsonAsync<JsonElement[]>("/api/presets");
        var hotel = Assert.Single(presets!);

        Assert.Equal(["emulator", "web", "client", "admin", "imager", "assets"],
            hotel.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("role").GetString()));
        Assert.Equal(["url", "badgeUrl"], hotel.GetProperty("roles")[5].GetProperty("exports").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("{\"name\":\"hotel\",\"preset\":\"turbo-nitro\",\"domain\":\"hotel.test\",\"tlsMode\":\"SelfSigned\",\"settings\":{}}", "Owner's username is required")]
    [InlineData("{\"name\":\"hotel\",\"preset\":\"turbo-nitro\",\"domain\":\"hotel.test\",\"tlsMode\":\"None\",\"settings\":{\"ownerName\":\"bill\"}}", "needs HTTPS")]
    [InlineData("{\"name\":\"hotel\",\"preset\":\"turbo-nitro\",\"domain\":\"hotel.test\",\"tlsMode\":\"SelfSigned\",\"hosts\":{\"client\":\"ws.hotel.test\"},\"settings\":{\"ownerName\":\"bill\"}}", "used for two parts")]
    [InlineData("{\"name\":\"hotel\",\"preset\":\"turbo-nitro\",\"domain\":\"hotel.test\",\"tlsMode\":\"SelfSigned\",\"hosts\":{\"assets\":\"\"},\"settings\":{\"ownerName\":\"bill\"}}", "assets.url")]
    [InlineData("{\"name\":\"hotel\",\"preset\":\"turbo-nitro\",\"domain\":\"not a domain\",\"settings\":{\"ownerName\":\"bill\"}}", "hotel's domain")]
    public async Task Bad_answers_are_explained(string json, string error)
    {
        var response = await _client.PostAsync("/api/stacks", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Installing_creates_wires_and_deploys_the_whole_hotel()
    {
        var stackId = await CreateAsync(Hotel());
        var job = await InstallAsync(stackId);

        Assert.Equal(["dotnet", "node"], _agent.Deploy.InstalledRuntimes.Order());
        Assert.Contains("hotel_emulator", _agent.Databases.Databases);

        var apps = await _client.GetFromJsonAsync<JsonElement[]>("/api/apps");
        Assert.Equal(["hotel-admin", "hotel-client", "hotel-emulator", "hotel-imager", "hotel-web"],
            apps!.Select(a => a.GetProperty("name").GetString()).Order());
        Assert.All(apps!, a => Assert.NotEqual(JsonValueKind.Null, a.GetProperty("currentRelease").ValueKind));
        Assert.Equal(2, (await _client.GetFromJsonAsync<JsonElement[]>("/api/repositories"))!.Length); // turbo-cloud, nitro-next

        // The wiring: every app knows where the others are.
        var client = await EnvironmentAsync("hotel-client");
        Assert.Equal("wss://ws.hotel.test", client["VITE_SOCKET_URL"]);
        Assert.Contains("\"asset.bundles.avatar\":\"https://cdn.example.com/bundled/figures/%libname%.nitro\"", client["NITRO_SITE_CONFIG"]);
        Assert.Contains("\"group.badge.url\":\"https://imager.hotel.test/badge/%imagerdata%.gif\"", client["NITRO_SITE_CONFIG"]);
        Assert.Equal("https://hotel.test/gamedata/external_variables/0", client["NITRO_EXTERNAL_VARIABLES"]);

        var emulator = await EnvironmentAsync("hotel-emulator");
        Assert.Equal("https://admin.hotel.test", emulator["TURBO__Turbo__Admin__PanelUrl"]);
        Assert.Equal("https://hotel.test", emulator["TURBO__Turbo__Web__SiteUrl"]);
        Assert.Equal("https://play.hotel.test/?sso={ticket}", emulator["TURBO__Turbo__Web__ClientUrl"]);
        Assert.Equal("https://imager.hotel.test/avatarimage?figure={figure}&size=l", emulator["TURBO__Turbo__Web__AvatarImageUrl"]);
        Assert.Equal("https://cdn.example.com/badges/%badgename%.gif", emulator["TURBO__Turbo__Achievements__BadgeAssetUrl"]);
        Assert.Equal("Test Hotel", emulator["TURBO__Turbo__Web__HotelName"]);
        Assert.Equal("bill", emulator["TURBO__Turbo__Owner__Name"]);

        Assert.Equal("https://cdn.example.com/bundled/templates/%libname%.nitro", (await EnvironmentAsync("hotel-imager"))["IMAGER_TEMPLATES_URL"]);
        Assert.Contains("proxy_pass http://127.0.0.1:8090;", _agent.Nginx.Sites["hotel-admin"]);
        Assert.Contains("frame-src https://play.hotel.test;", _agent.Nginx.Sites["hotel-web"]);
        Assert.Contains("img-src 'self' data: https://imager.hotel.test;", _agent.Nginx.Sites["hotel-web"]);
        Assert.Contains("server_name ws.hotel.test;", _agent.Nginx.Sites["hotel-emulator"]);
        Assert.False(_agent.Nginx.Sites.ContainsKey("hotel-assets")); // left out: an existing host is used

        // Nothing left to bring in step, and running the install again changes nothing.
        Assert.DoesNotContain((await _client.GetFromJsonAsync<JsonElement[]>($"/api/stacks/{stackId}/changes"))!,
            c => c.GetProperty("action").GetString() != "none" || c.GetProperty("environment").GetArrayLength() > 0);
        var again = await InstallAsync(stackId);
        Assert.Contains("hotel-emulator is live", again.GetProperty("output").GetString());
        Assert.Contains("① Runtimes", job.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Changing_a_domain_shows_and_applies_exactly_what_it_affects()
    {
        var stackId = await CreateAsync(Hotel());
        await InstallAsync(stackId);
        _agent.Services.Controls.Clear();

        var response = await _client.PutAsJsonAsync($"/api/stacks/{stackId}", new
        {
            tlsMode = "SelfSigned",
            hosts = new Dictionary<string, string> { ["client"] = "game.hotel.test", ["assets"] = "" },
        });
        var changes = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!.ToDictionary(c => c.GetProperty("role").GetString()!);

        Assert.Equal("reload", changes["client"].GetProperty("action").GetString()); // its own domain: nginx only
        Assert.Equal(["game.hotel.test"], changes["client"].GetProperty("domainsTo").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal("restart", changes["emulator"].GetProperty("action").GetString()); // knows the client's address
        Assert.Contains(changes["emulator"].GetProperty("environment").EnumerateArray(),
            c => c.GetProperty("key").GetString() == "TURBO__Turbo__Web__ClientUrl" && c.GetProperty("to").GetString() == "https://game.hotel.test/?sso={ticket}");
        Assert.Equal("reload", changes["web"].GetProperty("action").GetString()); // frames the client: its CSP changes
        Assert.Equal("none", changes["imager"].GetProperty("action").GetString());

        var job = await RunJobAsync(await _client.PostAsync($"/api/stacks/{stackId}/apply", null));
        Assert.True(job.GetProperty("status").GetString() == "succeeded", job.GetProperty("output").GetString());

        Assert.Contains("server_name game.hotel.test;", _agent.Nginx.Sites["hotel-client"]);
        Assert.Contains("frame-src https://game.hotel.test;", _agent.Nginx.Sites["hotel-web"]);
        Assert.Equal("https://game.hotel.test/?sso={ticket}", (await EnvironmentAsync("hotel-emulator"))["TURBO__Turbo__Web__ClientUrl"]);
        Assert.Equal(["tm-app-hotel-emulator.service"], _agent.Services.Controls.Select(c => c.Name));
        Assert.DoesNotContain((await _client.GetFromJsonAsync<JsonElement[]>($"/api/stacks/{stackId}/changes"))!,
            c => c.GetProperty("action").GetString() != "none");
    }

    [Fact]
    public async Task Values_edited_by_hand_or_pinned_on_the_hotel_are_kept()
    {
        var stackId = await CreateAsync(Hotel(overrides: new Dictionary<string, string> { ["imager.gamedataUrl"] = "https://data.example.com" }));
        await InstallAsync(stackId);

        Assert.Equal("https://data.example.com/gamedata/figuredata_json/0", (await EnvironmentAsync("hotel-imager"))["IMAGER_FIGUREDATA_URL"]);

        // Someone sets the emulator's client address by hand, then the client's domain changes.
        var apps = await _client.GetFromJsonAsync<JsonElement[]>("/api/apps");
        var emulatorId = apps!.Single(a => a.GetProperty("name").GetString() == "hotel-emulator").GetProperty("id").GetInt32();
        var environment = await EnvironmentAsync("hotel-emulator");
        environment["TURBO__Turbo__Web__ClientUrl"] = "https://custom.example.com/?sso={ticket}";
        (await _client.PutAsJsonAsync($"/api/apps/{emulatorId}/environment", environment)).EnsureSuccessStatusCode();

        var changes = await (await _client.PutAsJsonAsync($"/api/stacks/{stackId}", new
        {
            tlsMode = "SelfSigned",
            hosts = new Dictionary<string, string> { ["client"] = "game.hotel.test", ["assets"] = "" },
        })).Content.ReadFromJsonAsync<JsonElement[]>();
        var emulator = changes!.Single(c => c.GetProperty("role").GetString() == "emulator");
        Assert.True(emulator.GetProperty("environment").EnumerateArray()
            .Single(c => c.GetProperty("key").GetString() == "TURBO__Turbo__Web__ClientUrl").GetProperty("pinned").GetBoolean());

        await RunJobAsync(await _client.PostAsync($"/api/stacks/{stackId}/apply", null));

        environment = await EnvironmentAsync("hotel-emulator");
        Assert.Equal("https://custom.example.com/?sso={ticket}", environment["TURBO__Turbo__Web__ClientUrl"]); // kept
        Assert.Equal("https://game.hotel.test/?sso={ticket}", environment["TURBO__Turbo__Admin__ClientLoginUrl"]); // followed
    }

    [Fact]
    public async Task Answers_download_round_trips_to_the_same_hotel()
    {
        var stackId = await CreateAsync(Hotel(overrides: new Dictionary<string, string> { ["imager.maxLibraries"] = "100" }));

        var yaml = await _client.GetStringAsync($"/api/stacks/{stackId}/answers");
        var answers = TurboManager.Panel.Stacks.StackAnswers.Parse(yaml);

        Assert.StartsWith("# ", yaml);
        Assert.Equal("hotel.test", answers.Domain);
        Assert.Equal("play.hotel.test", answers.Hosts["client"]);
        Assert.Equal("", answers.Hosts["assets"]);
        Assert.Equal("https://cdn.example.com", answers.Settings["assets.url"]);
        Assert.Equal("100", answers.Overrides["imager.maxLibraries"]);
        Assert.Equal(TurboManager.Panel.Data.TlsMode.SelfSigned, answers.TlsMode);
    }

    [Fact]
    public async Task The_map_shows_roles_wiring_and_the_admin_setup_link()
    {
        var stackId = await CreateAsync(Hotel());
        await InstallAsync(stackId);
        _agent.Logs.Entries.Add(new JournalEntry
        {
            Unit = "tm-app-hotel-emulator.service",
            Message = "Admin panel setup for the owner, bill: open https://admin.hotel.test/setup#token=abc123 on the device you will sign in with.",
        });

        var map = await _client.GetFromJsonAsync<JsonElement>($"/api/stacks/{stackId}");
        var client = map.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("role").GetString() == "client");
        Assert.Equal("play.hotel.test", client.GetProperty("domain").GetString());
        Assert.Contains(client.GetProperty("wiring").EnumerateArray(),
            w => w.GetProperty("from").GetString() == "emulator.socketUrl" && w.GetProperty("value").GetString() == "wss://ws.hotel.test");
        Assert.False(map.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("role").GetString() == "assets").GetProperty("included").GetBoolean());

        var link = await _client.GetFromJsonAsync<JsonElement>($"/api/stacks/{stackId}/admin-setup-link");
        Assert.Equal("https://admin.hotel.test/setup#token=abc123", link.GetProperty("url").GetString());
    }
}

public sealed class UnattendedInstallTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private readonly string _answers = Path.Combine(Path.GetTempPath(), $"tm-hotel-{Guid.NewGuid():N}.yaml");
    private FakeAgent _agent = null!;

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(_answers, """
            name: hotel
            preset: turbo-nitro
            domain: hotel.test
            tlsMode: SelfSigned
            hosts:
              assets: ""
            settings:
              ownerName: bill
              assets.url: https://cdn.example.com
              assets.badgeUrl: https://cdn.example.com/badges/%badgename%.gif
            """);
        _panel.Settings["Panel:UnattendedConfig"] = _answers;
        _panel.Settings["Panel:DeployHealthTimeoutSeconds"] = "1";
        _panel.Settings["Panel:GitHubKnownHosts:0"] = "ssh-ed25519 AAAATESTKEY";
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
        File.Delete(_answers);
    }

    [Fact]
    public async Task A_hotel_answers_file_is_installed_without_anyone_signing_in()
    {
        var client = _panel.CreateApiClient(); // starts the panel

        for (var i = 0; i < 100 && _agent.Databases.Databases.Count == 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Contains("hotel_emulator", _agent.Databases.Databases);
        Assert.Contains("hotel", _agent.Deploy.Users);

        // Still unclaimed: the owner claims the panel (with 2FA) through the setup link as usual.
        var status = await client.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.False(status.GetProperty("claimed").GetBoolean());
    }
}
