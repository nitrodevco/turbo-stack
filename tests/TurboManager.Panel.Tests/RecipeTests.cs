using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TurboManager.Panel.Recipes;

namespace TurboManager.Panel.Tests;

public sealed class RecipeTemplateTests
{
    private static readonly Dictionary<string, string> Values = new()
    {
        ["input.url"] = "https://play.hotel.test/client/",
        ["input.empty"] = "",
        ["input.name"] = "My-Hotel",
        ["port.ws"] = "9001",
    };

    [Theory]
    [InlineData("ws://127.0.0.1:{{ port.ws }}", "ws://127.0.0.1:9001")]
    [InlineData("{{ input.url | origin }}", "https://play.hotel.test")]
    [InlineData("{{ input.url | host }}", "play.hotel.test")]
    [InlineData("{{ input.url | trimslash }}/?sso={ticket}", "https://play.hotel.test/client/?sso={ticket}")]
    [InlineData("{{ input.name | ident }}", "my_hotel")]
    [InlineData("{{ input.name | json }}", "\"My-Hotel\"")]
    [InlineData("{{ input.empty ?? 'Turbo' }}", "Turbo")]
    [InlineData("{{ input.url ? 'true' : 'false' }}", "true")]
    [InlineData("{{ input.empty ? 'true' : 'false' }}", "false")]
    public void Renders(string template, string expected) => Assert.Equal(expected, RecipeTemplate.Render(template, Values).Text);

    [Fact]
    public void Using_an_empty_value_is_reported_unless_guarded()
    {
        Assert.True(RecipeTemplate.Render("{{ input.empty }}/bundled", Values).UsedEmptyValue);
        Assert.False(RecipeTemplate.Render("{{ input.empty | origin ?? '' }}", Values).UsedEmptyValue);
        Assert.False(RecipeTemplate.Render("{{ input.url }}", Values).UsedEmptyValue);
    }

    [Theory]
    [InlineData("{{ input.missing }}")]
    [InlineData("{{ input.url | shout }}")]
    [InlineData("{{ input.url; rm -rf / }}")]
    [InlineData("{{ input.name | origin }}")]
    [InlineData("{{ input.url }")]
    public void Rejects_bad_templates(string template) =>
        Assert.Throws<RecipeTemplateException>(() => RecipeTemplate.Render(template, Values));
}

public sealed class TurboCryptoKeyTests
{
    [Fact]
    public void Generates_a_working_1024_bit_key_with_exponent_3()
    {
        var keys = TurboCryptoKeys.Generate();
        var n = BigInteger.Parse("0" + keys.Modulus, System.Globalization.NumberStyles.HexNumber);
        var d = BigInteger.Parse("0" + keys.PrivateExponent, System.Globalization.NumberStyles.HexNumber);

        Assert.Equal("3", keys.Exponent);
        Assert.Equal(1024, (int)n.GetBitLength());
        Assert.Equal(256, keys.Modulus.Length);

        var message = new BigInteger(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64), isUnsigned: true);
        Assert.Equal(message, BigInteger.ModPow(BigInteger.ModPow(message, 3, n), d, n));
        Assert.NotEqual(keys.Modulus, TurboCryptoKeys.Generate().Modulus);
    }

    [Fact]
    public void Primality_test_is_right_on_known_numbers()
    {
        Assert.True(TurboCryptoKeys.IsProbablePrime(BigInteger.Parse("170141183460469231731687303715884105727"))); // 2^127 - 1
        Assert.False(TurboCryptoKeys.IsProbablePrime(BigInteger.Parse("170141183460469231731687303715884105729")));
        Assert.False(TurboCryptoKeys.IsProbablePrime(561)); // Carmichael number
    }
}

public sealed class RecipeCatalogTests
{
    [Fact]
    public void Shipped_recipes_load_and_validate() =>
        Assert.Equal(
            ["dotnet-service", "nitro-client", "nitro-imager", "node-service", "static-assets", "static-site", "turbo-admin", "turbo-emulator", "turbo-web"],
            new RecipeCatalog().All.Select(r => r.Id).Order());

    [Theory]
    [InlineData("app: { build: '{{ input.nope }}' }", "Unknown value \"input.nope\"")]
    [InlineData("app: { service: { command: x, port: web } }", "isn't in ports")]
    [InlineData("app: { runtimes: [php] }", "unknown runtime")]
    [InlineData("site: { directory: true, routes: [{ path: /, kind: Teleport }] }", "unknown kind")]
    [InlineData("database: { engines: [oracle] }\napp: {}", "database engines")]
    [InlineData("secrets: [{ key: k, kind: magic }]\napp: {}", "secret \"k\"")]
    [InlineData("notes: []", "needs an app")]
    [InlineData("app: { bulid: x }", "bulid")]
    public void Mistakes_in_a_recipe_are_caught_when_it_loads(string body, string error)
    {
        var e = Assert.Throws<InvalidOperationException>(() => new RecipeCatalog([("test.yaml", $"id: test\nname: Test\n{body}")]));

        Assert.Contains(error, e.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RecipeApiTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _client = _panel.CreateApiClient();
        await _panel.CompleteSetupAsync(_client);
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private async Task<JsonElement> CreateAsync(string recipe, object request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _client.PostAsJsonAsync($"/api/recipes/{recipe}", request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<Dictionary<string, string>> EnvironmentAsync(int appId) =>
        (await _client.GetFromJsonAsync<Dictionary<string, string>>($"/api/apps/{appId}/environment"))!;

    [Fact]
    public async Task Lists_the_recipes_with_their_questions()
    {
        var recipes = await _client.GetFromJsonAsync<JsonElement>("/api/recipes");
        var emulator = recipes.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "turbo-emulator");

        Assert.Equal(["mysql", "mariadb"], emulator.GetProperty("databaseEngines").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(emulator.GetProperty("inputs").EnumerateArray(), i => i.GetProperty("key").GetString() == "adminPanelUrl");
    }

    [Fact]
    public async Task Turbo_emulator_gets_a_database_fresh_keys_shared_paths_and_a_websocket_site()
    {
        var created = await CreateAsync("turbo-emulator", new
        {
            name = "turbo",
            systemUser = "hotel",
            domains = new[] { "ws.hotel.test" },
            tlsMode = "None",
            inputs = new { hotelName = "Habbo", ownerName = "bill", adminPanelUrl = "https://admin.hotel.test/", clientUrl = "https://play.hotel.test" },
        });

        var appId = created.GetProperty("appId").GetInt32();
        var app = (await _client.GetFromJsonAsync<JsonElement>($"/api/apps/{appId}")).GetProperty("app");
        Assert.Equal("turbo-emulator", app.GetProperty("recipeId").GetString());
        Assert.Equal("nitrodevco/turbo-cloud", app.GetProperty("repository").GetString());
        Assert.Equal("out", app.GetProperty("serviceDirectory").GetString());
        Assert.Equal("dotnet Turbo.Main.dll", app.GetProperty("serviceCommand").GetString());
        Assert.Equal(9001, app.GetProperty("servicePort").GetInt32());
        Assert.Equal(8090, app.GetProperty("ports").GetProperty("admin").GetInt32());
        Assert.Contains("dotnet Turbo.Main.dll migrate", app.GetProperty("buildScript").GetString());

        Assert.Contains("turbo", _agent.Databases.Databases);
        var environment = await EnvironmentAsync(appId);
        Assert.Equal($"server=127.0.0.1;port=3306;user=turbo;password={_agent.Databases.Users["turbo"]};database=turbo",
            environment["TURBO__Turbo__Database__ConnectionString"]);
        Assert.Equal("true", environment["TURBO__Turbo__Admin__Enabled"]);
        Assert.Equal("https://admin.hotel.test", environment["TURBO__Turbo__Admin__PanelUrl"]);
        Assert.Equal("https://play.hotel.test/?sso={ticket}", environment["TURBO__Turbo__Web__ClientUrl"]);
        Assert.Equal("false", environment["TURBO__Turbo__Web__Enabled"]);
        Assert.False(environment.ContainsKey("TURBO__Turbo__Web__SiteUrl")); // empty input: Turbo's default applies
        Assert.Equal("/home/hotel/apps/turbo/shared/plugins", environment["TURBO__Turbo__Plugin__PluginFolderPath"]);
        Assert.Equal("3", environment["TURBO__Turbo__Crypto__KeySize"]);
        Assert.Equal(256, environment["TURBO__Turbo__Crypto__PublicKey"].Length);
        Assert.Equal(32, Convert.FromBase64String(environment["TURBO__Turbo__Assets__SecretKey"]).Length);
        Assert.Equal("127.0.0.1", environment["serverOptions__WebSocketServer__listeners__0__ip"]);

        var site = _agent.Nginx.Sites["turbo"];
        Assert.Contains("server_name ws.hotel.test;", site);
        Assert.Contains("proxy_pass http://127.0.0.1:9001;", site);
        Assert.Contains("Upgrade", site);
        Assert.Contains(created.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("wss://ws.hotel.test"));
    }

    [Fact]
    public async Task Apps_from_the_same_repository_share_one_checkout()
    {
        var emulator = await CreateAsync("turbo-emulator", new { name = "turbo", systemUser = "hotel" });
        var admin = await CreateAsync("turbo-admin", new { name = "admin", systemUser = "hotel", domains = new[] { "admin.hotel.test" }, tlsMode = "SelfSigned" });

        Assert.True(emulator.GetProperty("repositoryCreated").GetBoolean());
        Assert.False(admin.GetProperty("repositoryCreated").GetBoolean());
        Assert.Equal(emulator.GetProperty("repositoryId").GetInt32(), admin.GetProperty("repositoryId").GetInt32());

        var repository = (await _client.GetFromJsonAsync<JsonElement>($"/api/repositories/{admin.GetProperty("repositoryId").GetInt32()}"))
            .GetProperty("repository");
        Assert.True(repository.GetProperty("submodules").GetBoolean()); // turbo-admin is a submodule

        var site = _agent.Nginx.Sites["admin"];
        Assert.Contains("root /home/hotel/apps/admin/current/turbo-admin/dist;", site);
        Assert.Contains("proxy_pass http://127.0.0.1:8090;", site);
        Assert.Contains("Content-Security-Policy", site);
        Assert.Single(Regex.Matches(site, "proxy_read_timeout")); // nginx refuses a duplicate
    }

    [Fact]
    public async Task Ports_skip_ones_already_taken()
    {
        _agent.Firewall.ListeningPorts.Add(3030);
        var request = new { systemUser = "hotel", inputs = new { assetsUrl = "https://assets.hotel.test", gamedataUrl = "https://hotel.test" } };

        var first = await CreateAsync("nitro-imager", new { name = "imager", request.systemUser, request.inputs });
        var second = await CreateAsync("nitro-imager", new { name = "imager-2", request.systemUser, request.inputs });

        var environment = await EnvironmentAsync(first.GetProperty("appId").GetInt32());
        Assert.Equal("3031", environment["IMAGER_PORT"]);
        Assert.Equal("https://hotel.test/gamedata/figuredata_json/0", environment["IMAGER_FIGUREDATA_URL"]);
        Assert.Equal("3032", (await EnvironmentAsync(second.GetProperty("appId").GetInt32()))["IMAGER_PORT"]);

        var app = (await _client.GetFromJsonAsync<JsonElement>($"/api/apps/{first.GetProperty("appId").GetInt32()}")).GetProperty("app");
        Assert.Equal(1200, app.GetProperty("memoryMaxMb").GetInt32());
        Assert.Equal("/health", app.GetProperty("healthPath").GetString());
    }

    [Fact]
    public async Task Client_config_only_overrides_addresses_that_were_given()
    {
        var created = await CreateAsync("nitro-client", new
        {
            name = "client",
            systemUser = "hotel",
            domains = new[] { "play.hotel.test" },
            tlsMode = "None",
            inputs = new { socketUrl = "wss://ws.hotel.test", imagerUrl = "https://imager.hotel.test" },
        });

        var environment = await EnvironmentAsync(created.GetProperty("appId").GetInt32());
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(environment["NITRO_SITE_CONFIG"])!;
        Assert.Equal("wss://ws.hotel.test", environment["VITE_SOCKET_URL"]);
        Assert.Equal("wss://ws.hotel.test", config["socket.url"]);
        Assert.Equal("https://imager.hotel.test/badge/%imagerdata%.gif", config["group.badge.url"]);
        Assert.DoesNotContain("asset.bundles.avatar", config.Keys); // no assets address: the shipped one stays
        Assert.False(environment.ContainsKey("NITRO_EXTERNAL_VARIABLES"));
        Assert.DoesNotContain("X-Frame-Options", _agent.Nginx.Sites["client"]); // the public site frames it
    }

    [Fact]
    public async Task Assets_site_serves_an_uploaded_folder_with_cors()
    {
        var created = await CreateAsync("static-assets", new { name = "assets", systemUser = "hotel", domains = new[] { "assets.hotel.test" }, tlsMode = "None" });

        Assert.Equal(JsonValueKind.Null, created.GetProperty("appId").ValueKind);
        Assert.Contains("/home/hotel/sites/assets", _agent.Deploy.SiteDirectories);
        Assert.Contains("hotel", _agent.Deploy.Users);
        var site = _agent.Nginx.Sites["assets"];
        Assert.Contains("root /home/hotel/sites/assets;", site);
        Assert.Contains("Access-Control-Allow-Origin", site);
    }

    [Fact]
    public async Task Preview_changes_nothing_and_shows_no_secrets()
    {
        var response = await _client.PostAsJsonAsync("/api/recipes/turbo-emulator/preview", new { name = "turbo", systemUser = "hotel" });
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("(generated)", plan.GetProperty("app").GetProperty("environment").GetProperty("TURBO__Turbo__Crypto__PrivateKey").GetString());
        Assert.Contains("password=(generated)", plan.GetProperty("app").GetProperty("environment").GetProperty("TURBO__Turbo__Database__ConnectionString").GetString());
        Assert.Equal("mysql", plan.GetProperty("database").GetProperty("engine").GetString());
        Assert.Empty(_agent.Databases.Databases);
        Assert.Empty(_agent.Deploy.Users);
        Assert.Empty(await _client.GetFromJsonAsync<JsonElement[]>("/api/apps") ?? []);
    }

    [Theory]
    [InlineData("nitro-client", "{\"name\":\"client\",\"systemUser\":\"hotel\",\"domains\":[\"play.hotel.test\"]}", "Socket address is required")]
    [InlineData("nitro-client", "{\"name\":\"client\",\"systemUser\":\"hotel\",\"inputs\":{\"socketUrl\":\"wss://ws.hotel.test\"}}", "needs a domain")]
    [InlineData("turbo-emulator", "{\"name\":\"turbo\",\"systemUser\":\"hotel\",\"databaseEngine\":\"postgres\"}", "works with MySQL or MariaDB")]
    [InlineData("turbo-emulator", "{\"name\":\"turbo\",\"systemUser\":\"root\"}", "Linux user")]
    [InlineData("turbo-admin", "{\"name\":\"admin\",\"systemUser\":\"hotel\",\"domains\":[\"admin.hotel.test\"],\"tlsMode\":\"None\"}", "needs HTTPS")]
    [InlineData("nitro-imager", "{\"name\":\"imager\",\"systemUser\":\"hotel\",\"inputs\":{\"assetsUrl\":\"not a url\",\"gamedataUrl\":\"https://hotel.test\"}}", "full address")]
    public async Task Bad_answers_are_explained(string recipe, string json, string error)
    {
        var response = await _client.PostAsync($"/api/recipes/{recipe}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync());
        Assert.Empty(_agent.Databases.Databases);
    }

    [Fact]
    public async Task A_database_that_already_exists_is_not_reused()
    {
        _agent.Databases.Databases.Add("turbo");

        var response = await _client.PostAsJsonAsync("/api/recipes/turbo-emulator", new { name = "turbo", systemUser = "hotel" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already has a database called turbo", await response.Content.ReadAsStringAsync());
    }
}
