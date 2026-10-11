using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TurboManager.Core;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Http;
using TurboManager.Panel.Setup;

namespace TurboManager.Panel.Endpoints;

public static class SetupEndpoints
{
    public sealed record ClaimRequest(string? Token);

    public sealed record OwnerRequest(string? Username, string? Password);

    public sealed record ConfirmRequest(string? Code);

    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/setup");

        group.MapGet("/status", async (PanelDbContext db) =>
            Results.Ok(new { claimed = await IsClaimedAsync(db), productName = Branding.ProductName }));

        group.MapPost("/claim", async (ClaimRequest request, HttpContext http, PanelDbContext db, SetupTokenStore tokens, AuditLog audit,
            AuthFailureLog failures) =>
        {
            if (await IsClaimedAsync(db))
            {
                return AlreadyClaimed();
            }

            if (!tokens.Verify(request.Token))
            {
                failures.Record(http, "bad_setup_token");
                audit.Record("setup.claim_failed");
                await db.SaveChangesAsync();

                return Results.Problem("Invalid or expired setup token.", statusCode: StatusCodes.Status401Unauthorized);
            }

            var identity = new ClaimsIdentity([new Claim("tm:setup", "1")], AuthSchemes.Setup);
            await http.SignInAsync(AuthSchemes.Setup, new ClaimsPrincipal(identity));

            return Results.NoContent();
        }).RequireRateLimiting(RateLimits.Auth);

        group.MapPost("/owner", async (OwnerRequest request, HttpContext http, PanelDbContext db, TotpService totp,
            IPasswordHasher<PanelUser> hasher) =>
        {
            if (await IsClaimedAsync(db))
            {
                return AlreadyClaimed();
            }

            if (!(await http.AuthenticateAsync(AuthSchemes.Setup)).Succeeded)
            {
                return Results.Unauthorized();
            }

            var username = AccountRules.NormalizeUsername(request.Username);
            var error = AccountRules.ValidateUsername(username) ?? AccountRules.ValidatePassword(request.Password, username);

            if (error is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["owner"] = [error] });
            }

            // Restarting the wizard before 2FA was confirmed replaces the unfinished owner.
            db.Users.RemoveRange(await db.Users.Where(u => !u.TotpEnabled).ToListAsync());

            var owner = new PanelUser { Username = username, PasswordHash = "", Role = PanelRole.Owner };
            owner.PasswordHash = hasher.HashPassword(owner, request.Password!);

            var enrollment = totp.BeginEnrollment(owner, http.Request.Host.Host);
            db.Users.Add(owner);
            await db.SaveChangesAsync();

            return Results.Ok(new { secret = enrollment.Secret, otpauthUri = enrollment.OtpAuthUri });
        });

        group.MapPost("/owner/confirm", async (ConfirmRequest request, HttpContext http, PanelDbContext db, TotpService totp,
            SetupTokenStore tokens, AuditLog audit) =>
        {
            if (await IsClaimedAsync(db))
            {
                return AlreadyClaimed();
            }

            if (!(await http.AuthenticateAsync(AuthSchemes.Setup)).Succeeded)
            {
                return Results.Unauthorized();
            }

            var owner = await db.Users.Include(u => u.RecoveryCodes).FirstOrDefaultAsync(u => u.Role == PanelRole.Owner && !u.TotpEnabled);

            if (owner is null)
            {
                return Results.Problem("Create the owner account first.", statusCode: StatusCodes.Status409Conflict);
            }

            if (!totp.Verify(owner, request.Code))
            {
                return Results.Problem("That code is not valid. Check your authenticator app's clock and try again.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            owner.TotpEnabled = true;
            owner.LastLoginAt = DateTimeOffset.UtcNow;
            var recoveryCodes = RecoveryCodeService.Regenerate(owner);

            audit.Record("setup.completed", owner);
            await db.SaveChangesAsync();

            tokens.Consume();
            await http.SignOutAsync(AuthSchemes.Setup);
            await SessionManager.SignInAsync(http, AuthSchemes.Session, owner);

            return Results.Ok(new { recoveryCodes });
        }).RequireRateLimiting(RateLimits.Auth);
    }

    /// <summary>The panel is claimed once an owner with confirmed 2FA exists.</summary>
    public static Task<bool> IsClaimedAsync(PanelDbContext db) => db.Users.AnyAsync(u => u.TotpEnabled);

    private static IResult AlreadyClaimed() =>
        Results.Problem("This panel has already been set up.", statusCode: StatusCodes.Status409Conflict);
}
