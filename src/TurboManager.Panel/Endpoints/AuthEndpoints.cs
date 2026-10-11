using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Http;

namespace TurboManager.Panel.Endpoints;

public static class AuthEndpoints
{
    public const int MaxFailedAttempts = 10;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record MfaRequest(string? Code, string? RecoveryCode);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", async (LoginRequest request, HttpContext http, PanelDbContext db,
            IPasswordHasher<PanelUser> hasher, AuditLog audit, AuthFailureLog failures, TimeProvider time) =>
        {
            var now = time.GetUtcNow();
            var username = AccountRules.NormalizeUsername(request.Username);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);

            if (user is null || string.IsNullOrEmpty(request.Password))
            {
                failures.Record(http, "unknown_user", username);

                return InvalidCredentials();
            }

            if (user.LockedUntil > now)
            {
                return Locked();
            }

            if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            {
                RegisterFailure(user, now);
                failures.Record(http, "bad_password", username);
                audit.Record("auth.login_failed", user);
                await db.SaveChangesAsync();

                return InvalidCredentials();
            }

            if (!user.TotpEnabled)
            {
                return Results.Problem("This account has no second factor configured.", statusCode: StatusCodes.Status403Forbidden);
            }

            await SessionManager.SignInAsync(http, AuthSchemes.MfaPending, user);

            return Results.Ok(new { mfaRequired = true });
        }).RequireRateLimiting(RateLimits.Auth);

        group.MapPost("/mfa", async (MfaRequest request, HttpContext http, PanelDbContext db, TotpService totp,
            AuditLog audit, AuthFailureLog failures, TimeProvider time) =>
        {
            var now = time.GetUtcNow();
            var user = await SessionManager.GetUserAsync(http, AuthSchemes.MfaPending, db);

            if (user is null)
            {
                return Results.Problem("Your sign-in expired. Enter your password again.", statusCode: StatusCodes.Status401Unauthorized);
            }

            if (user.LockedUntil > now)
            {
                return Locked();
            }

            var usedRecoveryCode = false;
            var valid = !string.IsNullOrWhiteSpace(request.RecoveryCode)
                ? usedRecoveryCode = RecoveryCodeService.TryRedeem(user, request.RecoveryCode, now)
                : totp.Verify(user, request.Code);

            if (!valid)
            {
                RegisterFailure(user, now);
                failures.Record(http, "bad_second_factor", user.Username);
                audit.Record("auth.mfa_failed", user);
                await db.SaveChangesAsync();

                return Results.Problem("That code is not valid.", statusCode: StatusCodes.Status401Unauthorized);
            }

            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            user.LastLoginAt = now;
            audit.Record(usedRecoveryCode ? "auth.login_recovery_code" : "auth.login", user);
            await db.SaveChangesAsync();

            await http.SignOutAsync(AuthSchemes.MfaPending);
            await SessionManager.SignInAsync(http, AuthSchemes.Session, user);

            return Results.Ok(Me(user));
        }).RequireRateLimiting(RateLimits.Auth);

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AuthSchemes.Session);

            return Results.NoContent();
        });

        group.MapGet("/me", async (HttpContext http, PanelDbContext db) =>
        {
            var user = await SessionManager.GetUserAsync(http, AuthSchemes.Session, db);

            return user is null ? Results.Unauthorized() : Results.Ok(Me(user));
        });
    }

    private static object Me(PanelUser user) => new
    {
        username = user.Username,
        role = user.Role.ToString(),
        remainingRecoveryCodes = user.RecoveryCodes.Count(c => c.UsedAt is null),
        permissions = Permissions.For(user.Role),
    };

    private static void RegisterFailure(PanelUser user, DateTimeOffset now)
    {
        user.FailedLoginCount++;

        if (user.FailedLoginCount >= MaxFailedAttempts)
        {
            user.LockedUntil = now + LockoutDuration;
            user.FailedLoginCount = 0;
        }
    }

    private static IResult InvalidCredentials() =>
        Results.Problem("Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Locked() =>
        Results.Problem("Too many failed attempts. Try again later.", statusCode: StatusCodes.Status429TooManyRequests);
}
