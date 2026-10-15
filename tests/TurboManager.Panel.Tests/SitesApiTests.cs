using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TurboManager.Panel.Tests;

public sealed class SitesApiTests : IAsyncLifetime
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

    private static object Model(string name = "web", string tls = "SelfSigned", string domain = "myhotel.test", string? snippet = null) => new
    {
        name,
        domains = new[] { domain, $"www.{domain}" },
        tlsMode = tls,
        serverSnippet = snippet,
        routes = new object[]
        {
            new { path = "/", kind = "Spa", target = $"/var/www/{name}" },
            new { path = "api", kind = "Proxy", target = "http://127.0.0.1:8092" },
        },
    };

    private async Task<JsonElement> PostAsync(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task WaitForJobAsync(long jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await _client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");

            if (job.GetProperty("status").GetString() != "running")
            {
                Assert.Equal("succeeded", job.GetProperty("status").GetString());

                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Job did not finish");
    }

    [Fact]
    public async Task New_https_site_is_applied_http_first_then_switched_to_https_after_the_certificate_job()
    {
        var created = await PostAsync("/api/sites", Model());

        Assert.True(created.GetProperty("applied").GetBoolean());
        var jobId = created.GetProperty("certificateJobId").GetInt64();

        await WaitForJobAsync(jobId);

        Assert.Contains("listen 443 ssl http2;", _agent.Nginx.Sites["web"]);
        Assert.Contains("ssl_certificate /etc/turbo-stack/certs/myhotel.test/fullchain.pem;", _agent.Nginx.Sites["web"]);
        Assert.True(_agent.Nginx.Certificates.ContainsKey("myhotel.test"));

        var sites = await _client.GetFromJsonAsync<JsonElement>("/api/sites");
        Assert.True(sites[0].GetProperty("https").GetBoolean());

        var site = await _client.GetFromJsonAsync<JsonElement>($"/api/sites/{created.GetProperty("id").GetInt32()}");
        Assert.Equal(2, site.GetProperty("revisions").GetArrayLength());
        Assert.Equal("/api/", site.GetProperty("model").GetProperty("routes")[1].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Invalid_site_is_rejected_before_reaching_the_agent()
    {
        var response = await _client.PostAsJsonAsync("/api/sites", Model(domain: "not a domain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_agent.Nginx.Sites);
    }

    [Fact]
    public async Task Nginx_rejection_is_reported_and_recorded_without_losing_the_previous_config()
    {
        var created = await PostAsync("/api/sites", Model(tls: "None"));
        var id = created.GetProperty("id").GetInt32();
        var good = _agent.Nginx.Sites["web"];

        var response = await _client.PutAsJsonAsync($"/api/sites/{id}", Model(tls: "None", snippet: "BROKEN on;"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("applied").GetBoolean());
        Assert.Contains("unknown directive", body.GetProperty("error").GetString());
        Assert.Equal(good, _agent.Nginx.Sites["web"]);

        var site = await _client.GetFromJsonAsync<JsonElement>($"/api/sites/{id}");
        Assert.False(site.GetProperty("revisions")[0].GetProperty("applied").GetBoolean());
        Assert.Contains("unknown directive", site.GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task Restoring_a_revision_reapplies_its_model()
    {
        var created = await PostAsync("/api/sites", Model(tls: "None"));
        var id = created.GetProperty("id").GetInt32();
        await _client.PutAsJsonAsync($"/api/sites/{id}", Model(tls: "None", snippet: "add_header X-Version two;"));
        Assert.Contains("X-Version two", _agent.Nginx.Sites["web"]);

        var restored = await PostAsync($"/api/sites/{id}/revisions/1/restore", new { });

        Assert.True(restored.GetProperty("applied").GetBoolean());
        Assert.DoesNotContain("X-Version", _agent.Nginx.Sites["web"]);
    }

    [Fact]
    public async Task Domains_cannot_be_shared_between_sites()
    {
        await PostAsync("/api/sites", Model(name: "one", tls: "None"));

        var response = await _client.PostAsJsonAsync("/api/sites", Model(name: "two", tls: "None"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_site_removes_its_config()
    {
        var created = await PostAsync("/api/sites", Model(tls: "None"));

        var response = await _client.DeleteAsync($"/api/sites/{created.GetProperty("id").GetInt32()}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(_agent.Nginx.Sites.ContainsKey("web"));
    }

    [Fact]
    public async Task Custom_config_site_can_omit_routes()
    {
        var response = await _client.PostAsJsonAsync("/api/sites", new
        {
            name = "custom",
            domains = new[] { "custom.test" },
            tlsMode = "None",
            customConfig = "server { listen 80; server_name custom.test; }",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("server { listen 80; server_name custom.test; }\n", _agent.Nginx.Sites["custom"]);
    }

    [Fact]
    public async Task Preview_renders_without_applying()
    {
        var response = await _client.PostAsJsonAsync("/api/sites/preview", Model(tls: "None"));
        var config = await response.Content.ReadAsStringAsync();

        Assert.Contains("proxy_pass http://127.0.0.1:8092;", config);
        Assert.Empty(_agent.Nginx.Sites);
    }

    [Fact]
    public async Task Certificate_in_use_cannot_be_deleted()
    {
        var created = await PostAsync("/api/sites", Model());
        await WaitForJobAsync(created.GetProperty("certificateJobId").GetInt64());
        var certificates = await _client.GetFromJsonAsync<JsonElement>("/api/certificates");

        var response = await _client.DeleteAsync($"/api/certificates/{certificates[0].GetProperty("id").GetInt32()}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
