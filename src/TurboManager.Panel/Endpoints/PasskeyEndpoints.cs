using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Http;

namespace TurboManager.Panel.Endpoints;

public sealed record PasskeyFinish(string? Ceremony, JsonElement Response, string? Name = null);

/// <summary>Passkeys: signing in without a password, managing your passkeys, and confirming it's you with one.</summary>
public static class PasskeyEndpoints
{
    public static void MapPasskeyEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth/passkey").RequireRateLimiting(RateLimits.Auth);
        var self = app.MapGroup("/api/account").RequireAuthorization(policy => policy.AddAuthenticationSchemes(AuthSchemes.Session).RequireAuthenticatedUser());

        auth.MapPost("/options", async (HttpContext http, PasskeyService passkeys, CancellationToken cancellationToken) =>
            await Ceremony(async () =>
            {
                var (id, options) = await passkeys.BeginAssertionAsync(http, null, cancellationToken);

                return Results.Ok(new { ceremony = id, options });
            }));

        auth.MapPost("", async (PasskeyFinish request, HttpContext http, PanelDbContext db, PasskeyService passkeys, AuditLog audit,
            AuthFailureLog failures, TimeProvider time, CancellationToken cancellationToken) =>
            await Ceremony(async () =>
            {
                PanelUser user;

                try
                {
                    user = await passkeys.FinishAssertionAsync(http, request.Ceremony ?? "", request.Response, cancellationToken);
                }
                catch (PasskeyException)
                {
                    failures.Record(http, "bad_passkey", null);
                    throw;
                }

                var now = time.GetUtcNow();

                // A passkey checks possession and a PIN or biometric: both factors at once. The account must be fully set up.
                if (user.LockedUntil > now || !user.TotpEnabled)
                {
                    return Results.Problem("This account can't sign in right now.", statusCode: StatusCodes.Status403Forbidden);
                }

                user.FailedLoginCount = 0;
                user.LastLoginAt = now;
                audit.Record("auth.login_passkey", user);
                await db.SaveChangesAsync(cancellationToken);
                await SessionManager.SignInAsync(http, AuthSchemes.Session, user);

                return Results.Ok(new { username = user.Username });
            }));

        self.MapGet("/passkeys", async (HttpContext http, PanelDbContext db) =>
        {
            var user = await CurrentUser.GetAsync(http, db);

            return Results.Ok(new
            {
                // Passkeys can only be added on the panel's own domain.
                rpId = PasskeyService.RelyingParty(http),
                passkeys = await db.Passkeys.Where(p => p.UserId == user.Id).OrderBy(p => p.CreatedAt)
                    .Select(p => new { p.Id, p.Name, p.RpId, p.CreatedAt, p.LastUsedAt }).ToListAsync(),
            });
        });

        self.MapPost("/passkeys/options", async (HttpContext http, PanelDbContext db, PasskeyService passkeys) =>
            await Ceremony(async () =>
            {
                var user = await CurrentUser.GetAsync(http, db);
                var (id, options) = passkeys.BeginRegistration(http, user, await db.Passkeys.Where(p => p.UserId == user.Id).ToListAsync());
                await db.SaveChangesAsync(); // the user handle, made on first use

                return Results.Ok(new { ceremony = id, options });
            })).RequireSudo();

        self.MapPost("/passkeys", async (PasskeyFinish request, HttpContext http, PanelDbContext db, PasskeyService passkeys, AuditLog audit,
            CancellationToken cancellationToken) =>
            await Ceremony(async () =>
            {
                var name = (request.Name ?? "").Trim();

                if (name.Length is 0 or > 60)
                {
                    return Results.Problem("Name it (up to 60 characters), e.g. \"Laptop fingerprint\".", statusCode: StatusCodes.Status400BadRequest);
                }

                var user = await CurrentUser.GetAsync(http, db);
                var passkey = await passkeys.FinishRegistrationAsync(http, user, request.Ceremony ?? "", request.Response, name, cancellationToken);
                audit.Record("account.passkey_added", user, target: name, details: passkey.RpId);
                await db.SaveChangesAsync(cancellationToken);

                return Results.Ok(new { passkey.Id });
            })).RequireSudo();

        self.MapDelete("/passkeys/{id:int}", async (int id, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            var user = await CurrentUser.GetAsync(http, db);

            if (await db.Passkeys.FirstOrDefaultAsync(p => p.Id == id && p.UserId == user.Id) is not { } passkey)
            {
                return Results.NotFound();
            }

            db.Passkeys.Remove(passkey);
            audit.Record("account.passkey_removed", user, target: passkey.Name);
            await db.SaveChangesAsync();

            return Results.NoContent();
        }).RequireSudo();

        // "Confirm it's you" with a passkey instead of a code.
        self.MapPost("/sudo/passkey/options", async (HttpContext http, PanelDbContext db, PasskeyService passkeys, CancellationToken cancellationToken) =>
            await Ceremony(async () =>
            {
                var (id, options) = await passkeys.BeginAssertionAsync(http, await CurrentUser.GetAsync(http, db), cancellationToken);

                return Results.Ok(new { ceremony = id, options });
            }));

        self.MapPost("/sudo/passkey", async (PasskeyFinish request, HttpContext http, PanelDbContext db, PasskeyService passkeys, AuditLog audit,
            TimeProvider time, CancellationToken cancellationToken) =>
            await Ceremony(async () =>
            {
                var session = await SessionManager.GetSessionAsync(http, db);

                if (session is null)
                {
                    return Results.Unauthorized();
                }

                var user = await passkeys.FinishAssertionAsync(http, request.Ceremony ?? "", request.Response, cancellationToken);

                if (user.Id != session.UserId)
                {
                    return Results.Problem("That passkey belongs to someone else.", statusCode: StatusCodes.Status400BadRequest);
                }

                session.SudoUntil = time.GetUtcNow() + SessionManager.SudoDuration;
                audit.Record("auth.sudo_passkey", user);
                await db.SaveChangesAsync(cancellationToken);

                return Results.Ok(new { until = session.SudoUntil });
            }));
    }

    private static async Task<IResult> Ceremony(Func<Task<IResult>> work)
    {
        try
        {
            return await work();
        }
        catch (PasskeyException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
