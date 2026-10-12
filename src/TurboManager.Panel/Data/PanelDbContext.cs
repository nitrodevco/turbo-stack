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
        });

        model.Entity<AppRelease>(release =>
        {
            release.HasIndex(r => new { r.AppId, r.Name }).IsUnique();
            release.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            release.HasOne<App>().WithMany().HasForeignKey(r => r.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WebhookDelivery>().HasIndex(d => d.ReceivedAt);

        model.Entity<BackupSchedule>().HasIndex(s => new { s.Engine, s.Database }).IsUnique();

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

    private sealed class DateTimeOffsetToUnixMillisConverter()
        : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));
}
