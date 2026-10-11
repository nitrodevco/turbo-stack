using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TurboManager.Core;
using TurboManager.Panel;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Endpoints;
using TurboManager.Panel.Firewall;
using TurboManager.Panel.Http;
using TurboManager.Panel.Jobs;
using TurboManager.Panel.Metrics;
using TurboManager.Panel.Setup;
using TurboManager.Panel.Sites;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();

var panelOptions = builder.Configuration.GetSection(PanelOptions.Section).Get<PanelOptions>() ?? new PanelOptions();
var dataDirectory = Path.GetFullPath(panelOptions.DataDirectory);
Directory.CreateDirectory(dataDirectory);

builder.Services.Configure<PanelOptions>(builder.Configuration.GetSection(PanelOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<PanelDbContext>(db => db.UseSqlite($"Data Source={Path.Combine(dataDirectory, "panel.db")}"));
builder.Services.AddDataProtection()
    .SetApplicationName(Branding.Slug)
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

builder.Services.AddSingleton<SetupTokenStore>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<AgentChannelFactory>();
builder.Services.AddSingleton<IPasswordHasher<PanelUser>, PasswordHasher<PanelUser>>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddSingleton<AuthFailureLog>();
builder.Services.AddScoped<FirewallService>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService<MetricsCollector>();
builder.Services.AddHostedService<FirewallReconciler>();
builder.Services.AddScoped<CertificateService>();
builder.Services.AddScoped<SiteService>();
builder.Services.AddHostedService<CertificateRenewalService>();
builder.Services.AddHttpClient("detect-ip", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient(CertificateService.AcmeHttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // Only for test ACME servers such as Pebble; never enabled in production config.
        ServerCertificateCustomValidationCallback = panelOptions.AcmeSkipTlsVerify ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator : null,
    });
builder.Services.AddPanelRateLimits();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    // Only nginx on loopback is trusted (the default KnownProxies); it restores the real client IP.
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddAuthentication(AuthSchemes.Session)
    .AddCookie(AuthSchemes.Session, cookie => ConfigureCookie(cookie, "tm_session", TimeSpan.FromHours(12), sliding: true, validate: true))
    .AddCookie(AuthSchemes.MfaPending, cookie => ConfigureCookie(cookie, "tm_mfa", TimeSpan.FromMinutes(5), sliding: false, validate: false))
    .AddCookie(AuthSchemes.Setup, cookie => ConfigureCookie(cookie, "tm_setup", TimeSpan.FromMinutes(30), sliding: true, validate: false));
builder.Services.AddAuthorization(authorization => authorization.AddPermissionPolicies());

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
    await db.Database.MigrateAsync();
    await FirewallService.SeedAsync(db);

    if (panelOptions.DevGenerateSetupToken && !await SetupEndpoints.IsClaimedAsync(db))
    {
        var token = scope.ServiceProvider.GetRequiredService<SetupTokenStore>().Generate();
        app.Logger.LogWarning("Development setup URL: /setup?token={Token}", token);
    }
}

app.UseForwardedHeaders();
app.UseSecurityHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseCsrfHeaderCheck();
app.UseAuthentication();
app.UseAuthorization();

app.MapSetupEndpoints();
app.MapAuthEndpoints();
app.MapSystemEndpoints();
app.MapServicesEndpoints();
app.MapFirewallEndpoints();
app.MapAccessEndpoints();
app.MapPackagesEndpoints();
app.MapActivityEndpoints();
app.MapSitesEndpoints();
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

void ConfigureCookie(CookieAuthenticationOptions cookie, string name, TimeSpan lifetime, bool sliding, bool validate)
{
    cookie.Cookie.Name = name;
    cookie.Cookie.HttpOnly = true;
    cookie.Cookie.SameSite = SameSiteMode.Strict;
    cookie.Cookie.SecurePolicy = panelOptions.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    cookie.ExpireTimeSpan = lifetime;
    cookie.SlidingExpiration = sliding;

    // This is an API for a SPA: answer with status codes instead of redirecting to login pages.
    cookie.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        return Task.CompletedTask;
    };
    cookie.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    };

    if (validate)
    {
        cookie.Events.OnValidatePrincipal = SessionManager.ValidatePrincipalAsync;
    }
}

namespace TurboManager.Panel
{
    /// <summary>Entry-point marker for WebApplicationFactory in tests.</summary>
    public sealed class PanelEntryPoint;
}
