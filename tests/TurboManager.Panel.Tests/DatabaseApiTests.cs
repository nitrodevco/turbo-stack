using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TurboManager.Panel.Data;
using TurboManager.Panel.Databases;

namespace TurboManager.Panel.Tests;

public sealed class DatabaseApiTests : IAsyncLifetime
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

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Engines_are_listed_with_blocking_reasons()
    {
        var engines = await JsonAsync(await _client.GetAsync("/api/databases/engines"));

        Assert.Equal(["mysql", "mariadb", "postgres"], engines.EnumerateArray().Select(e => e.GetProperty("engine").GetString()));
        Assert.Equal("MySQL is installed", engines[1].GetProperty("blockedReason").GetString());
    }

    [Fact]
    public async Task Database_with_user_returns_credentials_once_and_keeps_them_encrypted()
    {
        var created = await JsonAsync(await _client.PostAsJsonAsync("/api/databases/mysql/databases", new { name = "turbo", user = "turbo" }));
        var credentials = created.GetProperty("credentials");
        var password = credentials.GetProperty("password").GetString()!;

        Assert.Equal(32, password.Length);
        Assert.Equal(3306, credentials.GetProperty("port").GetInt32());
        Assert.Equal(password, _agent.Databases.Users["turbo"]);
        Assert.Equal("all", _agent.Databases.Grants[("turbo", "turbo")]);

        var users = await JsonAsync(await _client.GetAsync("/api/databases/mysql/users"));
        Assert.True(users[0].GetProperty("passwordStored").GetBoolean());

        var revealed = await JsonAsync(await _client.GetAsync("/api/databases/mysql/users/turbo/password"));
        Assert.Equal(password, revealed.GetProperty("password").GetString());

        using var scope = _panel.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<PanelDbContext>().DatabaseCredentials.Single();
        Assert.DoesNotContain(password, stored.PasswordProtected);
    }

    [Fact]
    public async Task Failed_user_creation_rolls_back_the_new_database()
    {
        var response = await _client.PostAsJsonAsync("/api/databases/mysql/databases", new { name = "shop", user = "taken" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("shop", _agent.Databases.Databases);
    }

    [Fact]
    public async Task Dropping_a_database_takes_a_final_backup()
    {
        await _client.PostAsJsonAsync("/api/databases/mysql/databases", new { name = "old" });

        var dropped = await JsonAsync(await _client.DeleteAsync("/api/databases/mysql/databases/old"));

        Assert.StartsWith("mysql/old/", dropped.GetProperty("finalBackup").GetString());
        Assert.DoesNotContain("old", _agent.Databases.Databases);
    }

    [Fact]
    public async Task Password_reset_generates_and_stores_a_new_password()
    {
        await _client.PostAsJsonAsync("/api/databases/mysql/users", new { name = "app", password = "Initial-Passw0rd" });

        var reset = await JsonAsync(await _client.PutAsJsonAsync("/api/databases/mysql/users/app/password", new { }));
        var password = reset.GetProperty("password").GetString();

        Assert.NotEqual("Initial-Passw0rd", password);
        Assert.Equal(password, _agent.Databases.Users["app"]);
        Assert.Equal(password, (await JsonAsync(await _client.GetAsync("/api/databases/mysql/users/app/password"))).GetProperty("password").GetString());
    }

    [Fact]
    public async Task Imports_upload_and_backups_download_as_raw_bytes()
    {
        var upload = await JsonAsync(await _client.PostAsync("/api/databases/backups/upload?name=hotel.sql",
            new ByteArrayContent(Encoding.UTF8.GetBytes("CREATE TABLE users (id int);"))));
        Assert.Equal("imports/hotel.sql", upload.GetProperty("id").GetString());

        var download = await _client.GetAsync("/api/databases/backups/download?id=imports/hotel.sql");

        Assert.Equal("attachment; filename=\"hotel.sql\"", download.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal("CREATE TABLE users (id int);", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_backup_download_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/databases/backups/download?id=mysql/x/missing.sql.gz")).StatusCode);
    }

    [Fact]
    public async Task New_schedule_does_not_run_immediately_and_then_runs_with_retention()
    {
        await _client.PostAsJsonAsync("/api/databases/mysql/databases", new { name = "turbo" });
        var schedule = await JsonAsync(await _client.PutAsJsonAsync("/api/databases/mysql/databases/turbo/schedule",
            new { enabled = true, timeOfDay = "00:00", retentionCount = 2 }));
        Assert.True(schedule.GetProperty("enabled").GetBoolean());

        var scheduler = new BackupScheduler(_panel.Services.GetRequiredService<IServiceScopeFactory>(), _panel.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupScheduler>.Instance);

        await scheduler.RunDueAsync(CancellationToken.None);
        Assert.Empty(_agent.Databases.Backups);

        for (var day = 1; day <= 3; day++)
        {
            _panel.Time.Advance(TimeSpan.FromDays(1));
            await scheduler.RunDueAsync(CancellationToken.None);
        }

        Assert.Equal(2, _agent.Databases.Backups.Count);
    }

    [Theory]
    [InlineData("03:00", "2026-10-11T02:59:00+00:00", null, false)]
    [InlineData("03:00", "2026-10-11T03:00:00+00:00", null, true)]
    [InlineData("03:00", "2026-10-11T09:00:00+00:00", "2026-10-11T03:01:00+00:00", false)]
    [InlineData("03:00", "2026-10-12T03:05:00+00:00", "2026-10-11T03:01:00+00:00", true)]
    [InlineData("bogus", "2026-10-11T09:00:00+00:00", null, false)]
    public void Schedule_due_logic(string at, string now, string? lastRun, bool due)
    {
        var schedule = new BackupSchedule
        {
            Engine = "mysql",
            Database = "turbo",
            TimeOfDay = at,
            LastRunAt = lastRun is null ? null : DateTimeOffset.Parse(lastRun),
        };

        Assert.Equal(due, BackupScheduler.IsDue(schedule, DateTimeOffset.Parse(now)));
    }

    [Fact]
    public async Task Unknown_engine_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/databases/oracle/databases")).StatusCode);
    }
}
