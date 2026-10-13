using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TurboManager.Panel.Backups;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Tests;

public sealed class BackupApiTests : IAsyncLifetime
{
    private readonly PanelFactory _panel = new();
    private FakeAgent _agent = null!;
    private HttpClient _client = null!;
    private string _secret = "";

    public async Task InitializeAsync()
    {
        _agent = await FakeAgent.StartAsync(_panel.AgentSocket);
        _agent.Backups.PanelCopyPath = Path.Combine(_panel.DataDirectory, "backup", "panel.db");
        _client = _panel.CreateApiClient();
        (_secret, _) = await _panel.CompleteSetupAsync(_client);
    }

    public async Task DisposeAsync()
    {
        _panel.Dispose();
        await _agent.DisposeAsync();
    }

    private async Task SudoAsync()
    {
        _panel.Time.Advance(TimeSpan.FromSeconds(30));
        (await _client.PostAsJsonAsync("/api/account/sudo", new { code = _panel.CurrentTotp(_secret) })).EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> AddLocalTargetAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/backups/targets", new { name = "Second disk", kind = "Local", path = "/mnt/backup/hotel" });
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> WaitForJobAsync(long id)
    {
        for (var i = 0; i < 100; i++)
        {
            var job = await _client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}");

            if (job.GetProperty("status").GetString() != "running")
            {
                return job.GetProperty("status").GetString()!;
            }

            await Task.Delay(50);
        }

        return "timeout";
    }

    [Fact]
    public async Task A_target_is_opened_when_added_and_its_generated_password_is_shown_once()
    {
        var created = await AddLocalTargetAsync();
        var password = created.GetProperty("password").GetString()!;

        Assert.True(password.Length >= 32);
        var prepared = Assert.Single(_agent.Backups.Prepared);
        Assert.Equal(("local", "/mnt/backup/hotel", password), (prepared.Kind, prepared.Path, prepared.Password));
        Assert.True(created.GetProperty("target").GetProperty("ready").GetBoolean());

        var listed = await _client.GetStringAsync("/api/backups");
        Assert.DoesNotContain(password, listed);

        var id = created.GetProperty("target").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync($"/api/backups/targets/{id}/password")).StatusCode);
        await SudoAsync();
        Assert.Equal(password, (await _client.GetFromJsonAsync<JsonElement>($"/api/backups/targets/{id}/password")).GetProperty("password").GetString());
    }

    [Fact]
    public async Task A_target_that_cannot_be_opened_is_not_kept()
    {
        _agent.Backups.FailPrepare = "The repository can't be opened or created: wrong password or no key found";

        var response = await _client.PostAsJsonAsync("/api/backups/targets",
            new { name = "Old repo", kind = "Local", path = "/mnt/old", password = "not-the-right-password" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("wrong password", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, (await _client.GetFromJsonAsync<JsonElement>("/api/backups")).GetProperty("targets").GetArrayLength());
    }

    [Fact]
    public async Task An_sftp_target_gets_its_own_key_and_is_used_only_after_its_host_key_is_confirmed()
    {
        var response = await _client.PostAsJsonAsync("/api/backups/targets",
            new { name = "Storage box", kind = "Sftp", endpoint = "u123.your-storagebox.de", port = 23, user = "u123", path = "hotel" });
        response.EnsureSuccessStatusCode();
        var target = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("target");
        var id = target.GetProperty("id").GetInt32();

        Assert.StartsWith("ssh-ed25519 ", target.GetProperty("publicKey").GetString());
        Assert.False(target.GetProperty("ready").GetBoolean());
        Assert.Empty(_agent.Backups.Prepared);

        var scanned = await _client.GetFromJsonAsync<JsonElement>($"/api/backups/targets/{id}/host-key");
        Assert.Equal("SHA256:abc", scanned.GetProperty("fingerprint").GetString());

        var confirmed = await _client.PostAsJsonAsync($"/api/backups/targets/{id}/host-key", new { hostKey = scanned.GetProperty("hostKey").GetString() });
        confirmed.EnsureSuccessStatusCode();
        Assert.True((await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ready").GetBoolean());
        Assert.Equal("ssh-ed25519 AAAAhost", Assert.Single(_agent.Backups.Prepared).HostKey);
    }

    [Fact]
    public async Task A_plan_runs_as_a_job_with_a_fresh_copy_of_the_panel_database()
    {
        var target = (await AddLocalTargetAsync()).GetProperty("target").GetProperty("id").GetInt32();

        var invalid = await _client.PostAsJsonAsync("/api/backups/plans", new { name = "Nightly", targetId = target, folders = new[] { "hotel:../etc" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var created = await _client.PostAsJsonAsync("/api/backups/plans", new
        {
            name = "Nightly", targetId = target, databases = new[] { "mariadb/turbo" }, folders = new[] { "hotel:sites/assets" }, panelState = true,
            keepDaily = 7, keepWeekly = 4, keepMonthly = 0,
        });
        created.EnsureSuccessStatusCode();
        var plan = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var run = await _client.PostAsync($"/api/backups/plans/{plan}/run", null);
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        Assert.Equal("succeeded", await WaitForJobAsync((await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetInt64()));

        var sent = Assert.Single(_agent.Backups.Runs);
        Assert.Equal(("mariadb", "turbo"), (sent.Databases[0].Engine, sent.Databases[0].Name));
        Assert.Equal(("hotel", "sites/assets"), (sent.Folders[0].User, sent.Folders[0].Path));
        Assert.Equal((7, 4, 0), (sent.Retention.Daily, sent.Retention.Weekly, sent.Retention.Monthly));
        Assert.Equal($"p{plan}", sent.PlanId);
        Assert.True(Assert.Single(_agent.Backups.PanelCopySeen), "the database copy is there while the agent backs it up");
        Assert.False(File.Exists(_agent.Backups.PanelCopyPath), "and gone afterwards");

        var saved = (await _client.GetFromJsonAsync<JsonElement>("/api/backups")).GetProperty("plans")[0];
        Assert.Equal("0123abcd4567", saved.GetProperty("lastSnapshot").GetString());
        Assert.Equal("OK", saved.GetProperty("lastResult").GetString());
    }

    [Fact]
    public async Task Restoring_needs_a_fresh_sign_in_and_only_sends_what_was_chosen()
    {
        var target = (await AddLocalTargetAsync()).GetProperty("target").GetProperty("id").GetInt32();
        var manifest = await _client.GetFromJsonAsync<JsonElement>($"/api/backups/targets/{target}/snapshots/0123abcd");
        Assert.Equal("mariadb/turbo", manifest.GetProperty("databases")[0].GetString());

        var body = new { databases = new[] { "mariadb/turbo" }, folders = Array.Empty<string>(), panelState = false };
        var response = await _client.PostAsJsonAsync($"/api/backups/targets/{target}/snapshots/0123abcd/restore", body);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await SudoAsync();
        response = await _client.PostAsJsonAsync($"/api/backups/targets/{target}/snapshots/0123abcd/restore", body);
        Assert.Equal("succeeded", await WaitForJobAsync((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetInt64()));

        var restore = Assert.Single(_agent.Backups.Restores);
        Assert.Equal("0123abcd", restore.SnapshotId);
        Assert.Single(restore.Databases);
        Assert.Empty(restore.Folders);
        Assert.False(restore.PanelState);
    }

    [Fact]
    public async Task A_target_in_use_by_a_plan_cannot_be_removed()
    {
        var target = (await AddLocalTargetAsync()).GetProperty("target").GetProperty("id").GetInt32();
        (await _client.PostAsJsonAsync("/api/backups/plans", new { name = "Panel", targetId = target, panelState = true })).EnsureSuccessStatusCode();
        await SudoAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.DeleteAsync($"/api/backups/targets/{target}")).StatusCode);
    }

    [Fact]
    public async Task A_plan_without_a_good_run_for_days_raises_an_alert_until_it_succeeds()
    {
        var target = (await AddLocalTargetAsync()).GetProperty("target").GetProperty("id").GetInt32();
        var plan = (await (await _client.PostAsJsonAsync("/api/backups/plans", new { name = "Nightly", targetId = target, panelState = true, enabled = true }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        async Task<string[]> OpenAlertsAsync()
        {
            (await _client.PostAsync("/api/alerts/check", null)).EnsureSuccessStatusCode();

            return (await _client.GetFromJsonAsync<JsonElement>("/api/alerts")).GetProperty("open").EnumerateArray()
                .Select(a => a.GetProperty("title").GetString()!).ToArray();
        }

        Assert.DoesNotContain(await OpenAlertsAsync(), t => t.Contains("Nightly"));
        // Made three days ago (moving the clock that far would also end the session).
        using (var scope = _panel.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
            (await db.BackupPlans.FindAsync(plan))!.CreatedAt = _panel.Time.GetUtcNow().AddDays(-3);
            await db.SaveChangesAsync();
        }

        Assert.Contains("Backup plan Nightly has never succeeded", await OpenAlertsAsync());

        var run = await _client.PostAsync($"/api/backups/plans/{plan}/run", null);
        Assert.Equal("succeeded", await WaitForJobAsync((await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetInt64()));
        Assert.DoesNotContain(await OpenAlertsAsync(), t => t.Contains("Nightly"));
    }

    [Fact]
    public async Task Only_the_owner_restores_the_panel_itself_after_confirming()
    {
        var target = (await AddLocalTargetAsync()).GetProperty("target").GetProperty("id").GetInt32();
        var path = $"/api/backups/targets/{target}/snapshots/0123abcd/restore-panel";

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync(path, null)).StatusCode);
        await SudoAsync();
        var response = await _client.PostAsync(path, null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(["0123abcd"], _agent.Backups.PanelRestores);
        Assert.Contains("restarts", await response.Content.ReadAsStringAsync());
        Assert.Contains("backups.panel_restore", await _client.GetStringAsync("/api/audit?action=backups.panel_restore"));
    }

    [Fact]
    public void Plans_run_once_a_day_after_their_time()
    {
        var plan = new BackupPlan { Name = "n", TimeOfDay = "03:00" };
        var at = new DateTimeOffset(2026, 10, 12, 3, 0, 30, TimeSpan.Zero);

        Assert.False(BackupService.IsDue(plan, at.AddMinutes(-5)));
        Assert.True(BackupService.IsDue(plan, at));
        plan.LastRunAt = at;
        Assert.False(BackupService.IsDue(plan, at.AddHours(5)));
        Assert.True(BackupService.IsDue(plan, at.AddDays(1)));
        plan.Enabled = false;
        Assert.False(BackupService.IsDue(plan, at.AddDays(1)));
    }
}
