using Microsoft.EntityFrameworkCore;

namespace TurboManager.Panel.Data;

public sealed class PanelDbContext(DbContextOptions<PanelDbContext> options) : DbContext(options)
{
    public DbSet<PanelUser> Users => Set<PanelUser>();

    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<MetricSample> MetricSamples => Set<MetricSample>();

    public DbSet<ManagedFirewallRule> FirewallRules => Set<ManagedFirewallRule>();

    public DbSet<IpList> IpLists => Set<IpList>();

    public DbSet<FirewallSettings> FirewallSettings => Set<FirewallSettings>();

    public DbSet<FirewallChangeRecord> FirewallChanges => Set<FirewallChangeRecord>();

    public DbSet<Site> Sites => Set<Site>();

    public DbSet<SiteRevision> SiteRevisions => Set<SiteRevision>();

    public DbSet<ManagedCertificate> Certificates => Set<ManagedCertificate>();

    public DbSet<AcmeAccount> AcmeAccounts => Set<AcmeAccount>();

    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();

    public DbSet<CloudflareAccount> CloudflareAccounts => Set<CloudflareAccount>();

    public DbSet<PanelAddress> PanelAddresses => Set<PanelAddress>();

    public DbSet<DatabaseCredential> DatabaseCredentials => Set<DatabaseCredential>();

    public DbSet<BackupSchedule> BackupSchedules => Set<BackupSchedule>();

    public DbSet<GitRepository> Repositories => Set<GitRepository>();

    public DbSet<App> Apps => Set<App>();

    public DbSet<AppRelease> Releases => Set<AppRelease>();

    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    public DbSet<Stack> Stacks => Set<Stack>();

    public DbSet<PanelSession> Sessions => Set<PanelSession>();

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    public DbSet<AlertChannel> AlertChannels => Set<AlertChannel>();

    public DbSet<AlertSettings> AlertSettings => Set<AlertSettings>();

    public DbSet<PanelUpdateState> PanelUpdates => Set<PanelUpdateState>();

    public DbSet<BackupTarget> BackupTargets => Set<BackupTarget>();

    public DbSet<BackupPlan> BackupPlans => Set<BackupPlan>();

    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<PanelUser>(user =>
        {
            user.HasIndex(u => u.Username).IsUnique();
            user.Property(u => u.Username).HasMaxLength(32);
            user.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
            user.HasMany(u => u.RecoveryCodes).WithOne().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<AuditEntry>(audit =>
        {
            audit.HasIndex(a => a.At);
            audit.Property(a => a.Action).HasMaxLength(64);
        });

        model.Entity<Job>(job =>
        {
            job.HasIndex(j => j.StartedAt);
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
        });

        model.Entity<MetricSample>().HasIndex(m => m.AtUnixMs);

        model.Entity<ManagedFirewallRule>(rule =>
        {
            rule.HasIndex(r => r.Key).IsUnique();
            rule.Property(r => r.SourceKind).HasConversion<string>().HasMaxLength(16);
            rule.HasOne(r => r.IpList).WithMany().HasForeignKey(r => r.IpListId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<IpList>(list =>
        {
            list.HasIndex(l => l.Name).IsUnique();
            list.HasMany(l => l.Entries).WithOne().HasForeignKey(e => e.IpListId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Site>(site =>
        {
            site.HasIndex(s => s.Name).IsUnique();
            site.Property(s => s.TlsMode).HasConversion<string>().HasMaxLength(24);
            site.HasMany(s => s.Routes).WithOne().HasForeignKey(r => r.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SiteRoute>().Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);

        model.Entity<SiteRevision>(revision =>
        {
            revision.HasIndex(r => new { r.SiteId, r.Number }).IsUnique();
            revision.HasOne<Site>().WithMany().HasForeignKey(r => r.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ManagedCertificate>(certificate =>
        {
            certificate.HasIndex(c => c.Name).IsUnique();
            certificate.Property(c => c.Kind).HasConversion<string>().HasMaxLength(24);
            certificate.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        });

        model.Entity<DatabaseCredential>().HasIndex(c => new { c.Engine, c.Username }).IsUnique();

        model.Entity<GitRepository>(repository =>
        {
            repository.HasIndex(r => new { r.SystemUser, r.Name }).IsUnique();
            repository.Property(r => r.Auth).HasConversion<string>().HasMaxLength(16);
            repository.OwnsMany(r => r.SubmoduleKeys, keys => keys.ToJson());
            repository.Ignore(r => r.FullName);
        });

        model.Entity<App>(app =>
        {
            app.HasIndex(a => a.Name).IsUnique();
            app.HasOne(a => a.Repository).WithMany().HasForeignKey(a => a.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            app.OwnsMany(a => a.SharedPaths, paths => paths.ToJson());
            app.Ignore(a => a.AppDirectory);
            app.Property(a => a.RecipeInputs).HasConversion(JsonConverter<Dictionary<string, string>>(), JsonComparer<Dictionary<string, string>>());
            app.Property(a => a.Ports).HasConversion(JsonConverter<Dictionary<string, int>>(), JsonComparer<Dictionary<string, int>>());
        });

        model.Entity<AppRelease>(release =>
        {
            release.HasIndex(r => new { r.AppId, r.Name }).IsUnique();
            release.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            release.HasOne<App>().WithMany().HasForeignKey(r => r.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WebhookDelivery>().HasIndex(d => d.ReceivedAt);

        model.Entity<AlertChannel>().Property(c => c.Kind).HasConversion<string>().HasMaxLength(16);
        model.Entity<AlertEvent>(alert =>
        {
            alert.Property(a => a.Severity).HasConversion<string>().HasMaxLength(16);
            alert.HasIndex(a => new { a.Key, a.ResolvedAt });
            alert.HasIndex(a => a.FiredAt);
        });

        model.Entity<PanelSession>(session =>
        {
            session.HasIndex(s => s.UserId);
            session.HasOne<PanelUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ApiToken>(token =>
        {
            token.HasIndex(t => t.Hash).IsUnique();
            token.HasOne<PanelUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Stack>(stack =>
        {
            stack.HasIndex(s => s.Name).IsUnique();
            stack.Property(s => s.TlsMode).HasConversion<string>().HasMaxLength(24);
            stack.Property(s => s.Hosts).HasConversion(JsonConverter<Dictionary<string, string>>(), JsonComparer<Dictionary<string, string>>());
            stack.Property(s => s.Settings).HasConversion(JsonConverter<Dictionary<string, string>>(), JsonComparer<Dictionary<string, string>>());
            stack.Property(s => s.Overrides).HasConversion(JsonConverter<Dictionary<string, string>>(), JsonComparer<Dictionary<string, string>>());
            stack.Property(s => s.Sources).HasConversion(JsonConverter<List<StackSource>>(), JsonComparer<List<StackSource>>());
        });

        model.Entity<BackupSchedule>().HasIndex(s => new { s.Engine, s.Database }).IsUnique();

        model.Entity<BackupTarget>(target =>
        {
            target.HasIndex(t => t.Name).IsUnique();
            target.Property(t => t.Kind).HasConversion<string>().HasMaxLength(16);
        });

        model.Entity<BackupPlan>(plan =>
        {
            plan.HasIndex(p => p.Name).IsUnique();
            plan.HasOne<BackupTarget>().WithMany().HasForeignKey(p => p.TargetId).OnDelete(DeleteBehavior.Restrict);
            plan.Property(p => p.Databases).HasConversion(JsonConverter<List<string>>(), JsonComparer<List<string>>());
            plan.Property(p => p.Folders).HasConversion(JsonConverter<List<string>>(), JsonComparer<List<string>>());
        });

        model.Entity<FirewallChangeRecord>(change =>
        {
            change.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            change.HasIndex(c => c.Status);
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder conventions)
    {
        // SQLite cannot order/compare DateTimeOffset natively; store as unix milliseconds.
        conventions.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUnixMillisConverter>();
    }


    /// <summary>Stores a small object as a JSON string (dictionaries aren't primitive collections in EF).</summary>
    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
            v => string.IsNullOrEmpty(v) ? new T() : System.Text.Json.JsonSerializer.Deserialize<T>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new T());

    /// <summary>Compares by JSON so changes inside the object are saved.</summary>
    private static Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<T> JsonComparer<T>() where T : new() =>
        new((a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
            v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(StringComparison.Ordinal),
            v => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null), (System.Text.Json.JsonSerializerOptions?)null)!);
    private sealed class DateTimeOffsetToUnixMillisConverter()
        : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));
}
