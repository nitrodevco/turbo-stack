using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TurboManager.Contracts.Agent;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Cloudflare;
using TurboManager.Panel.Data;

namespace TurboManager.Panel.Sites;

/// <summary>Issues and tracks TLS certificates; the agent installs them under /etc/turbo-manager/certs.</summary>
public sealed class CertificateService(
    PanelDbContext db,
    AgentChannelFactory agent,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpClients,
    CloudflareService cloudflare,
    CloudflareClient cloudflareApi,
    IOptions<PanelOptions> options,
    TimeProvider time,
    ILogger<CertificateService> logger)
{
    public const string CertificateRoot = "/etc/turbo-manager/certs";

    /// <summary>Certificates closer to expiry than this are renewed (and not used for new sites).</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private readonly IDataProtector _protector = dataProtection.CreateProtector("TurboManager.Acme.v1");

    public static string NameFor(IReadOnlyList<string> domains) =>
        domains[0].StartsWith("*.", StringComparison.Ordinal) ? $"wildcard.{domains[0][2..]}" : domains[0];

    /// <summary>True when <paramref name="certificateDomains"/> covers <paramref name="domain"/> (exact, or a one-level wildcard).</summary>
    public static bool Covers(IEnumerable<string> certificateDomains, string domain) =>
        certificateDomains.Any(c => c.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || (c.StartsWith("*.", StringComparison.Ordinal) && domain.IndexOf('.') is > 0 and var dot
                && domain[(dot + 1)..].Equals(c[2..], StringComparison.OrdinalIgnoreCase)));

    public static CertificatePaths PathsFor(ManagedCertificate certificate) =>
        new($"{CertificateRoot}/{certificate.Name}/fullchain.pem", $"{CertificateRoot}/{certificate.Name}/privkey.pem");

    /// <summary>A valid certificate covering every domain of the site, preferring the site's TLS mode.</summary>
    public async Task<ManagedCertificate?> FindForAsync(Site site)
    {
        var now = time.GetUtcNow();
        var candidates = await db.Certificates.Where(c => c.Status == CertificateStatus.Valid).ToListAsync();

        return candidates
            .Where(c => c.NotAfter > now && site.Domains.All(d => Covers(c.Domains, d)))
            .OrderByDescending(c => KindFor(site.TlsMode) == c.Kind)
            .ThenByDescending(c => c.NotAfter)
            .FirstOrDefault();
    }

    public static CertificateKind? KindFor(TlsMode mode) => mode switch
    {
        TlsMode.LetsEncrypt => CertificateKind.LetsEncrypt,
        TlsMode.CloudflareOrigin => CertificateKind.CloudflareOrigin,
        TlsMode.SelfSigned => CertificateKind.SelfSigned,
        _ => null,
    };

    /// <summary>Finds or creates the record for a certificate covering exactly these domains.</summary>
    public async Task<ManagedCertificate> GetOrCreateAsync(IReadOnlyList<string> domains, CertificateKind kind)
    {
        var name = NameFor(domains);
        var existing = await db.Certificates.FirstOrDefaultAsync(c => c.Name == name);

        if (existing is not null)
        {
            existing.Domains = [.. domains];
            existing.Kind = kind;

            return existing;
        }

        var certificate = new ManagedCertificate { Name = name, Domains = [.. domains], Kind = kind };
        db.Certificates.Add(certificate);
        await db.SaveChangesAsync();

        return certificate;
    }

    /// <summary>Issues (or renews) a certificate and installs it through the agent.</summary>
    public async Task<bool> IssueAsync(ManagedCertificate certificate, Action<string> log, CancellationToken cancellationToken)
    {
        certificate.LastAttemptAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            log($"Issuing a {certificate.Kind} certificate for {string.Join(", ", certificate.Domains)}");

            var (chain, key) = certificate.Kind switch
            {
                CertificateKind.SelfSigned => CreateSelfSigned(certificate.Domains),
                CertificateKind.LetsEncrypt => await IssueAcmeAsync(certificate.Domains, log, cancellationToken),
                CertificateKind.CloudflareOrigin => await IssueCloudflareOriginAsync(certificate.Domains, log, cancellationToken),
                _ => throw new InvalidOperationException($"{certificate.Kind} certificates can't be issued yet."),
            };

            var installed = await agent.Nginx.InstallCertificateAsync(
                new InstallCertificateRequest { Name = certificate.Name, FullchainPem = chain, PrivateKeyPem = key }, cancellationToken: cancellationToken);

            certificate.Status = CertificateStatus.Valid;
            certificate.NotAfter = DateTimeOffset.FromUnixTimeMilliseconds(installed.NotAfterUnixMs);
            certificate.IssuedAt = time.GetUtcNow();
            certificate.LastError = null;
            await db.SaveChangesAsync(cancellationToken);
            log($"Installed; valid until {certificate.NotAfter:yyyy-MM-dd}.");

            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Certificate {Name} could not be issued", certificate.Name);
            certificate.Status = certificate.NotAfter > time.GetUtcNow() ? CertificateStatus.Valid : CertificateStatus.Failed;
            certificate.LastError = e is AcmeRequestException acme ? acme.Error?.Detail ?? acme.Message : e.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            log($"Failed: {certificate.LastError}");

            return false;
        }
    }

    /// <summary>Cloudflare Origin CA: trusted by Cloudflare's edge only (Full (strict) mode), valid for 15 years.</summary>
    private async Task<(string Chain, string Key)> IssueCloudflareOriginAsync(IReadOnlyList<string> domains, Action<string> log,
        CancellationToken cancellationToken)
    {
        var (_, token) = await cloudflare.RequireAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={domains[0]}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();

        foreach (var domain in domains)
        {
            san.AddDnsName(domain);
        }

        request.CertificateExtensions.Add(san.Build());
        log("Requesting a Cloudflare Origin certificate…");
        var issued = await cloudflareApi.CreateOriginCertificateAsync(token, request.CreateSigningRequestPem(), domains, cancellationToken);

        return (issued.Certificate.Trim() + "\n", key.ExportPkcs8PrivateKeyPem());
    }

    public static (string Chain, string Key) CreateSelfSigned(IReadOnlyList<string> domains)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={domains[0]}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();

        foreach (var domain in domains)
        {
            san.AddDnsName(domain);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));

        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    private async Task<(string Chain, string Key)> IssueAcmeAsync(IReadOnlyList<string> domains, Action<string> log, CancellationToken cancellationToken)
    {
        var acme = await GetAcmeContextAsync();
        var order = await acme.NewOrder([.. domains]);
        var written = new List<AcmeChallenge>();

        try
        {
            foreach (var authorization in await order.Authorizations())
            {
                var resource = await authorization.Resource();

                if (resource.Status == AuthorizationStatus.Valid)
                {
                    continue;
                }

                var http = await authorization.Http();
                var challenge = new AcmeChallenge { Token = http.Token, KeyAuthorization = http.KeyAuthz };
                await agent.Nginx.WriteAcmeChallengeAsync(challenge, cancellationToken: cancellationToken);
                written.Add(challenge);

                log($"Validating {resource.Identifier.Value} over HTTP…");
                await http.Validate();
                await WaitForAuthorizationAsync(authorization, resource.Identifier.Value, cancellationToken);
            }

            log("All domains validated; requesting the certificate…");
            var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var chain = await order.Generate(new CsrInfo { CommonName = domains[0] }, key);

            return (chain.ToPem(), key.ToPem());
        }
        finally
        {
            foreach (var challenge in written)
            {
                try
                {
                    await agent.Nginx.RemoveAcmeChallengeAsync(challenge, cancellationToken: CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger.LogDebug(e, "Could not remove ACME challenge {Token}", challenge.Token);
                }
            }
        }
    }

    private static async Task WaitForAuthorizationAsync(IAuthorizationContext authorization, string domain, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var resource = await authorization.Resource();

            if (resource.Status == AuthorizationStatus.Valid)
            {
                return;
            }

            if (resource.Status == AuthorizationStatus.Invalid)
            {
                var detail = resource.Challenges?.Select(c => c.Error?.Detail).FirstOrDefault(d => d is not null);

                throw new InvalidOperationException(
                    $"{domain} could not be validated: {detail ?? "unknown error"}. Check that its DNS points to this server and port 80 is open.");
            }
        }

        throw new TimeoutException($"Validation of {domain} timed out.");
    }

    private async Task<AcmeContext> GetAcmeContextAsync()
    {
        var directory = new Uri(options.Value.AcmeDirectory);
        var account = await db.AcmeAccounts.FirstOrDefaultAsync();
        var http = new AcmeHttpClient(directory, httpClients.CreateClient(AcmeHttpClientName));

        if (account is not null && account.DirectoryUrl == directory.ToString())
        {
            return new AcmeContext(directory, KeyFactory.FromPem(_protector.Unprotect(account.KeyProtected)), http);
        }

        var context = new AcmeContext(directory, http: http);
        await context.NewAccount(account?.Email is { Length: > 0 } email ? [$"mailto:{email}"] : [], termsOfServiceAgreed: true);

        if (account is null)
        {
            account = new AcmeAccount { DirectoryUrl = directory.ToString(), KeyProtected = "" };
            db.AcmeAccounts.Add(account);
        }

        account.DirectoryUrl = directory.ToString();
        account.KeyProtected = _protector.Protect(context.AccountKey.ToPem());
        await db.SaveChangesAsync();

        return context;
    }

    public const string AcmeHttpClientName = "acme";

    /// <summary>Re-reads installed certificates from the agent (e.g. after a restore).</summary>
    public async Task<CertificateList> ListInstalledAsync(CancellationToken cancellationToken) =>
        await agent.Nginx.ListCertificatesAsync(new Empty(), cancellationToken: cancellationToken);
}
