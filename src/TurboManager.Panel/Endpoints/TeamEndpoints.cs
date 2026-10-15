using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.Audit;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Http;

namespace TurboManager.Panel.Endpoints;

public sealed record InviteRequest(string? Username, PanelRole Role, List<int>? ScopeStacks = null, List<int>? ScopeApps = null, int? CustomRoleId = null);

public sealed record MemberUpdate(PanelRole Role, List<int>? ScopeStacks = null, List<int>? ScopeApps = null, int? CustomRoleId = null);

public sealed record CustomRoleRequest(string? Name, string? Description, List<string>? Permissions);

public sealed record InvitePasswordRequest(string? Token, string? Password);

public sealed record InviteConfirmRequest(string? Token, string? Code);

public sealed record SudoRequest(string? Code, string? RecoveryCode);

public sealed record TokenRequest(string? Name, List<string>? Permissions, int? ExpiresInDays);

/// <summary>The panel's users: invites, roles and scopes, sessions, API tokens and re-authentication.</summary>
public static partial class TeamEndpoints
{
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(3);

    public static void MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var manage = app.MapGroup("/api/team").RequirePermission(Permissions.UsersManage);
        // Anyone signed in (a session, not a token): their own sessions and tokens.
        var self = app.MapGroup("/api/account").RequireAuthorization(policy => policy.AddAuthenticationSchemes(AuthSchemes.Session).RequireAuthenticatedUser());

        manage.MapGet("", async (PanelDbContext db) =>
        {
            var users = await db.Users.OrderBy(u => u.Role).ThenBy(u => u.Username).ToListAsync();
            var sessions = await db.Sessions.GroupBy(s => s.UserId).Select(g => new { g.Key, last = g.Max(s => s.LastSeenAt), count = g.Count() }).ToListAsync();

            return Results.Ok(users.Select(u => new
            {
                u.Id,
                u.Username,
                role = u.Role.ToString(),
                u.CustomRoleId,
                twoFactor = u.TotpEnabled,
                invitePending = u.InviteHash is not null,
                u.InviteExpiresAt,
                u.InvitedBy,
                u.LastLoginAt,
                u.CreatedAt,
                u.ScopeStacks,
                u.ScopeApps,
                activeSessions = sessions.FirstOrDefault(s => s.Key == u.Id)?.count ?? 0,
            }));
        });

        manage.MapPost("", async (InviteRequest request, HttpContext http, PanelDbContext db, IPasswordHasher<PanelUser> hasher, AuditLog audit) =>
        {
            var actor = await CurrentUser.GetAsync(http, db);
            var username = AccountRules.NormalizeUsername(request.Username);

            if (AccountRules.ValidateUsername(username) is { } error)
            {
                return Invalid(error);
            }

            if (request.Role == PanelRole.Owner)
            {
                return Invalid("There is one owner. Invite an admin instead.");
            }

            if (await CustomRoleErrorAsync(request.Role, request.CustomRoleId, db) is { } roleError)
            {
                return Invalid(roleError);
            }

            if (await db.Users.AnyAsync(u => u.Username == username))
            {
                return Invalid($"{username} already exists.");
            }

            var user = new PanelUser
            {
                Username = username,
                PasswordHash = "",
                Role = request.Role,
                CustomRoleId = request.Role == PanelRole.Custom ? request.CustomRoleId : null,
                ScopeStacks = request.ScopeStacks ?? [],
                ScopeApps = request.ScopeApps ?? [],
                InvitedBy = actor.Username,
            };
            // Unusable until the invite sets a real one.
            user.PasswordHash = hasher.HashPassword(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var link = Invite(user, http);
            db.Users.Add(user);
            audit.Record("team.invited", actor, target: username, details: user.Role.ToString());
            await db.SaveChangesAsync();

            return Results.Ok(new { user.Id, inviteUrl = link });
        }).RequireSudo();

        manage.MapPut("/{id:int}", async (int id, MemberUpdate request, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            var actor = await CurrentUser.GetAsync(http, db);
            var user = await db.Users.FindAsync(id);

            if (user is null)
            {
                return Results.NotFound();
            }

            if (user.Role == PanelRole.Owner || request.Role == PanelRole.Owner || user.Id == actor.Id)
            {
                return Invalid("The owner's role, and your own, can't be changed here.");
            }

            if (await CustomRoleErrorAsync(request.Role, request.CustomRoleId, db) is { } roleError)
            {
                return Invalid(roleError);
            }

            var customRoleId = request.Role == PanelRole.Custom ? request.CustomRoleId : null;
            var changed = user.Role != request.Role || user.CustomRoleId != customRoleId;
            user.Role = request.Role;
            user.CustomRoleId = customRoleId;
            user.ScopeStacks = request.ScopeStacks ?? [];
            user.ScopeApps = request.ScopeApps ?? [];

            if (changed)
            {
                // New permissions take effect now: sign the user's browsers out.
                user.SecurityStamp = Guid.NewGuid().ToString("N");
                await db.Sessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync();
            }

            audit.Record("team.updated", actor, target: user.Username, details: $"{user.Role}; hotels [{string.Join(",", user.ScopeStacks)}] apps [{string.Join(",", user.ScopeApps)}]");
            await db.SaveChangesAsync();

            return Results.NoContent();
        }).RequireSudo();

        // Lost password or authenticator: a new invite link replaces both.
        manage.MapPost("/{id:int}/reset", async (int id, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            var actor = await CurrentUser.GetAsync(http, db);
            var user = await db.Users.Include(u => u.RecoveryCodes).FirstOrDefaultAsync(u => u.Id == id);

            if (user is null)
            {
                return Results.NotFound();
            }

            if (user.Role == PanelRole.Owner || user.Id == actor.Id)
            {
                return Invalid("The owner recovers with recovery codes (or ts-panel-recover on the server).");
            }

            user.TotpEnabled = false;
            user.TotpSecretProtected = null;
            user.RecoveryCodes.Clear();
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await db.Sessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync();
            await db.ApiTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync();
            var link = Invite(user, http);
            audit.Record("team.reset", actor, target: user.Username);
            await db.SaveChangesAsync();

            return Results.Ok(new { inviteUrl = link });
        }).RequireSudo();

        manage.MapDelete("/{id:int}", async (int id, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            var actor = await CurrentUser.GetAsync(http, db);
            var user = await db.Users.FindAsync(id);

            if (user is null)
            {
                return Results.NotFound();
            }

            if (user.Role == PanelRole.Owner || user.Id == actor.Id)
            {
                return Invalid("The owner, and yourself, can't be removed.");
            }

            db.Users.Remove(user); // sessions and tokens go with it
            audit.Record("team.removed", actor, target: user.Username);
            await db.SaveChangesAsync();

            return Results.NoContent();
        }).RequireSudo();

        // Custom roles: a name and a set of permissions from the matrix.
        manage.MapGet("/roles", async (PanelDbContext db) =>
        {
            var members = await db.Users.Where(u => u.CustomRoleId != null).GroupBy(u => u.CustomRoleId).Select(g => new { g.Key, count = g.Count() })
                .ToListAsync();

            return Results.Ok(new
            {
                catalog = Permissions.Catalog.Select(c => new
                {
                    permission = c.Permission, area = c.Area, description = c.Description, customAllowed = !Permissions.NotForCustomRoles.Contains(c.Permission),
                }),
                builtIn = Enum.GetValues<PanelRole>().Where(r => r != PanelRole.Custom).Select(r => new { role = r.ToString(), permissions = Permissions.For(r) }),
                custom = (await db.CustomRoles.OrderBy(r => r.Name).ToListAsync()).Select(r => new
                {
                    r.Id, r.Name, r.Description, r.Permissions, members = members.FirstOrDefault(m => m.Key == r.Id)?.count ?? 0,
                }),
            });
        });

        manage.MapPost("/roles", async (CustomRoleRequest request, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            if (await RoleRequestErrorAsync(request, null, db) is { } error)
            {
                return Invalid(error);
            }

            var role = new CustomRole { Name = request.Name!.Trim(), Description = (request.Description ?? "").Trim(), Permissions = [.. request.Permissions!.Distinct()] };
            db.CustomRoles.Add(role);
            audit.Record("team.role_created", await CurrentUser.GetAsync(http, db), target: role.Name, details: string.Join(", ", role.Permissions));
            await db.SaveChangesAsync();

            return Results.Ok(new { role.Id });
        }).RequireSudo();

        manage.MapPut("/roles/{id:int}", async (int id, CustomRoleRequest request, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            if (await db.CustomRoles.FindAsync(id) is not { } role)
            {
                return Results.NotFound();
            }

            if (await RoleRequestErrorAsync(request, id, db) is { } error)
            {
                return Invalid(error);
            }

            // Members get the new permissions on their next request (they're read from here every time).
            role.Name = request.Name!.Trim();
            role.Description = (request.Description ?? "").Trim();
            role.Permissions = [.. request.Permissions!.Distinct()];
            audit.Record("team.role_changed", await CurrentUser.GetAsync(http, db), target: role.Name, details: string.Join(", ", role.Permissions));
            await db.SaveChangesAsync();

            return Results.NoContent();
        }).RequireSudo();

        manage.MapDelete("/roles/{id:int}", async (int id, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            if (await db.CustomRoles.FindAsync(id) is not { } role)
            {
                return Results.NotFound();
            }

            if (await db.Users.AnyAsync(u => u.CustomRoleId == id))
            {
                return Invalid("Members still have this role; give them another one first.");
            }

            db.CustomRoles.Remove(role);
            audit.Record("team.role_deleted", await CurrentUser.GetAsync(http, db), target: role.Name);
            await db.SaveChangesAsync();

            return Results.NoContent();
        }).RequireSudo();

        manage.MapGet("/sessions", async (PanelDbContext db) =>
            Results.Ok(await db.Sessions.Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { session = s, u.Username })
                .OrderByDescending(x => x.session.LastSeenAt)
                .Select(x => new { x.session.Id, x.Username, x.session.CreatedAt, x.session.LastSeenAt, x.session.IpAddress, x.session.UserAgent })
                .ToListAsync()));

        manage.MapDelete("/sessions/{sessionId}", async (string sessionId, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            var session = await db.Sessions.FindAsync(sessionId);

            if (session is null)
            {
                return Results.NotFound();
            }

            db.Sessions.Remove(session);
            audit.Record("team.session_revoked", await CurrentUser.GetAsync(http, db), details: $"user {session.UserId}");
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        // ----- Invites (no session: the link is the credential) -----

        var invite = app.MapGroup("/api/invite").RequireRateLimiting(RateLimits.Auth);

        invite.MapGet("/{token}", async (string token, PanelDbContext db, TimeProvider time) =>
            await FindInviteAsync(db, token, time) is { } user
                ? Results.Ok(new { user.Username, role = user.Role.ToString(), user.InvitedBy })
                : InviteGone());

        invite.MapPost("/password", async (InvitePasswordRequest request, HttpContext http, PanelDbContext db, IPasswordHasher<PanelUser> hasher,
            TotpService totp, TimeProvider time) =>
        {
            if (await FindInviteAsync(db, request.Token, time) is not { } user)
            {
                return InviteGone();
            }

            if (AccountRules.ValidatePassword(request.Password, user.Username) is { } error)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [error] });
            }

            user.PasswordHash = hasher.HashPassword(user, request.Password!);
            var enrollment = totp.BeginEnrollment(user, http.Request.Host.Host);
            await db.SaveChangesAsync();

            return Results.Ok(new { secret = enrollment.Secret, otpauthUri = enrollment.OtpAuthUri });
        });

        invite.MapPost("/confirm", async (InviteConfirmRequest request, HttpContext http, PanelDbContext db, TotpService totp, AuditLog audit,
            TimeProvider time) =>
        {
            if (await FindInviteAsync(db, request.Token, time) is not { } user || user.TotpSecretProtected is null)
            {
                return InviteGone();
            }

            if (!totp.Verify(user, request.Code))
            {
                return Results.Problem("That code is not valid. Check your authenticator app's clock and try again.", statusCode: StatusCodes.Status400BadRequest);
            }

            user.TotpEnabled = true;
            user.InviteHash = null;
            user.InviteExpiresAt = null;
            user.LastLoginAt = time.GetUtcNow();
            var recoveryCodes = RecoveryCodeService.Regenerate(user);
            audit.Record("team.joined", user);
            await db.SaveChangesAsync();
            await SessionManager.SignInAsync(http, AuthSchemes.Session, user);

            return Results.Ok(new { recoveryCodes });
        });

        // ----- Your own account -----

        self.MapPost("/sudo", async (SudoRequest request, HttpContext http, PanelDbContext db, TotpService totp, AuditLog audit, TimeProvider time) =>
        {
            var user = await CurrentUser.GetAsync(http, db);
            var session = await SessionManager.GetSessionAsync(http, db);
            var now = time.GetUtcNow();

            if (session is null)
            {
                return Results.Unauthorized();
            }

            var valid = !string.IsNullOrWhiteSpace(request.RecoveryCode)
                ? RecoveryCodeService.TryRedeem(user, request.RecoveryCode, now)
                : totp.Verify(user, request.Code);

            if (!valid)
            {
                audit.Record("auth.sudo_failed", user);
                await db.SaveChangesAsync();

                return Results.Problem("That code is not valid.", statusCode: StatusCodes.Status400BadRequest);
            }

            session.SudoUntil = now + SessionManager.SudoDuration;
            audit.Record("auth.sudo", user);
            await db.SaveChangesAsync();

            return Results.Ok(new { until = session.SudoUntil });
        }).RequireRateLimiting(RateLimits.Auth);

        self.MapGet("/sessions", async (HttpContext http, PanelDbContext db) =>
        {
            var user = await CurrentUser.GetAsync(http, db);
            var current = http.User.FindFirstValue(AuthSchemes.SessionIdClaim);

            return Results.Ok(await db.Sessions.Where(s => s.UserId == user.Id).OrderByDescending(s => s.LastSeenAt)
                .Select(s => new { s.Id, s.CreatedAt, s.LastSeenAt, s.IpAddress, s.UserAgent, current = s.Id == current }).ToListAsync());
        });

        self.MapDelete("/sessions/{sessionId}", async (string sessionId, HttpContext http, PanelDbContext db) =>
        {
            var user = await CurrentUser.GetAsync(http, db);
            var deleted = await db.Sessions.Where(s => s.Id == sessionId && s.UserId == user.Id).ExecuteDeleteAsync();

            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });

        self.MapGet("/tokens", async (HttpContext http, PanelDbContext db) =>
        {
            var user = await CurrentUser.GetAsync(http, db);

            return Results.Ok(await db.ApiTokens.Where(t => t.UserId == user.Id).OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.Name, t.Prefix, t.Permissions, t.CreatedAt, t.ExpiresAt, t.LastUsedAt }).ToListAsync());
        });

        self.MapPost("/tokens", async (TokenRequest request, HttpContext http, PanelDbContext db, AuditLog audit, TimeProvider time) =>
        {
            var user = await CurrentUser.GetAsync(http, db);
            var name = (request.Name ?? "").Trim();
            var permissions = (request.Permissions ?? []).Distinct().ToList();

            if (name.Length is 0 or > 60)
            {
                return Invalid("Give the token a name (up to 60 characters), e.g. \"GitHub Actions\".");
            }

            var allowed = Permissions.For(user, user.CustomRoleId is { } roleId ? await db.CustomRoles.FindAsync(roleId) : null);

            if (permissions.Count == 0 || permissions.Any(p => !allowed.Contains(p)))
            {
                return Invalid(permissions.Count == 0 ? "Choose what the token may do." : "A token can only do what your role can.");
            }

            if (permissions.Contains(Permissions.TerminalRoot) || permissions.Contains(Permissions.UsersManage))
            {
                return Invalid("Tokens can't open a root terminal or manage users.");
            }

            var secret = ApiTokenAuthenticationHandler.NewToken();
            var token = new ApiToken
            {
                UserId = user.Id,
                Name = name,
                Prefix = secret[..10],
                Hash = ApiTokenAuthenticationHandler.HashOf(secret),
                Permissions = permissions,
                ExpiresAt = request.ExpiresInDays is > 0 and <= 3650 ? time.GetUtcNow().AddDays(request.ExpiresInDays.Value) : null,
            };
            db.ApiTokens.Add(token);
            audit.Record("account.token_created", user, target: name, details: string.Join(", ", permissions));
            await db.SaveChangesAsync();

            return Results.Ok(new { token.Id, token = secret });
        }).RequireSudo();

        self.MapDelete("/tokens/{id:int}", async (int id, HttpContext http, PanelDbContext db, AuditLog audit) =>
        {
            var user = await CurrentUser.GetAsync(http, db);
            var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == user.Id);

            if (token is null)
            {
                return Results.NotFound();
            }

            db.ApiTokens.Remove(token);
            audit.Record("account.token_deleted", user, target: token.Name);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });
    }

    private static async Task<string?> CustomRoleErrorAsync(PanelRole role, int? customRoleId, PanelDbContext db) =>
        role != PanelRole.Custom ? null
        : customRoleId is { } id && await db.CustomRoles.AnyAsync(r => r.Id == id) ? null
        : "Choose which custom role.";

    private static async Task<string?> RoleRequestErrorAsync(CustomRoleRequest request, int? id, PanelDbContext db)
    {
        var name = (request.Name ?? "").Trim();
        var permissions = request.Permissions ?? [];

        if (!RoleNamePattern().IsMatch(name) || Enum.GetNames<PanelRole>().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return "Role names: 1-40 letters, digits, spaces, dots or dashes, and not a built-in role's name.";
        }

        if (permissions.Count == 0 || permissions.Any(p => !Permissions.All.Contains(p)))
        {
            return "Choose at least one permission from the matrix.";
        }

        if (permissions.FirstOrDefault(Permissions.NotForCustomRoles.Contains) is { } reserved)
        {
            return $"{reserved} stays with the owner and admins.";
        }

        return await db.CustomRoles.AnyAsync(r => r.Name == name && r.Id != id) ? $"There is already a role called {name}." : null;
    }

    private static string Invite(PanelUser user, HttpContext http)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        user.InviteHash = Hash(token);
        user.InviteExpiresAt = DateTimeOffset.UtcNow + InviteLifetime;

        return $"{http.Request.Scheme}://{http.Request.Host}/invite?token={token}";
    }

    private static async Task<PanelUser?> FindInviteAsync(PanelDbContext db, string? token, TimeProvider time)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = Hash(token);
        var user = await db.Users.Include(u => u.RecoveryCodes).FirstOrDefaultAsync(u => u.InviteHash == hash);

        return user?.InviteExpiresAt > time.GetUtcNow() ? user : null;
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static IResult InviteGone() =>
        Results.Problem("This invite link is invalid or has expired. Ask for a new one.", statusCode: StatusCodes.Status404NotFound);

    private static IResult Invalid(string message) => Results.ValidationProblem(new Dictionary<string, string[]> { ["team"] = [message] });

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,39}$")]
    private static partial System.Text.RegularExpressions.Regex RoleNamePattern();
}
