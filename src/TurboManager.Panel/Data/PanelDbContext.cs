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
