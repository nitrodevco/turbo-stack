using Microsoft.EntityFrameworkCore;
using TurboManager.Panel.AgentClient;
using TurboManager.Panel.Auth;
using TurboManager.Panel.Data;
using TurboManager.Panel.Http;
using TurboManager.Panel.Recipes;

namespace TurboManager.Panel.Endpoints;

public static class RecipeEndpoints
{
    public static void MapRecipeEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/recipes").WithAgentErrors().RequirePermission(Permissions.AppsView);
        // Recipes also create sites (and databases, checked per recipe).
        var manage = app.MapGroup("/api/recipes").WithAgentErrors().RequirePermission(Permissions.AppsManage).RequirePermission(Permissions.SitesManage);

        view.MapGet("", (RecipeCatalog catalog) => Results.Ok(catalog.All.Select(r => new
        {
            r.Id,
            r.Name,
            r.Summary,
            r.Description,
            r.Category,
            source = r.Source,
            inputs = r.Inputs,
            databaseEngines = r.Database?.Engines,
            ports = r.Ports,
            hasApp = r.App is not null,
            hasService = r.App?.Service is not null,
            runtimes = r.App?.Runtimes ?? [],
            site = r.Site is null ? null : new { r.Site.Required, r.Site.RequiresTls, r.Site.Directory },
        })));

        manage.MapPost("/{id}/preview", async (string id, RecipeRequest request, HttpContext http, RecipeCatalog catalog, RecipeService recipes,
            CancellationToken cancellationToken) =>
        {
            if (catalog.Find(id) is not { } recipe)
            {
                return Results.NotFound();
            }

            if (Forbidden(recipe, http) is { } forbidden)
            {
                return forbidden;
            }

            try
            {
                return Results.Ok(await recipes.PlanAsync(recipe, request, preview: true, cancellationToken));
            }
            catch (RecipeException e)
            {
                return Invalid(e.Message);
            }
        });

        manage.MapPost("/{id}", async (string id, RecipeRequest request, HttpContext http, PanelDbContext db, RecipeCatalog catalog,
            RecipeService recipes, CancellationToken cancellationToken) =>
        {
            if (catalog.Find(id) is not { } recipe)
            {
                return Results.NotFound();
            }

            if (Forbidden(recipe, http) is { } forbidden)
            {
                return forbidden;
            }

            try
            {
                var result = await recipes.CreateAsync(recipe, request, await CurrentUser.GetAsync(http, db), cancellationToken);
                var repository = result.RepositoryId is { } repositoryId ? await db.Repositories.FirstAsync(r => r.Id == repositoryId, cancellationToken) : null;

                return Results.Ok(new
                {
                    result.AppId,
                    result.SiteId,
                    result.RepositoryId,
                    result.RepositoryCreated,
                    // Private repositories need their deploy keys added on GitHub before the first deploy.
                    deployKeys = repository is { Auth: RepositoryAuth.DeployKey }
                        ? repository.SubmoduleKeys.Select(k => new { repository = $"{k.Owner}/{k.Name}", publicKey = k.PublicKey })
                            .Prepend(new { repository = repository.FullName, publicKey = repository.PublicKey })
                        : null,
                    site = result.Site,
                    result.Notes,
                });
            }
            catch (RecipeException e)
            {
                return Invalid(e.Message);
            }
        });
    }

    private static IResult? Forbidden(RecipeManifest recipe, HttpContext http) =>
        recipe.Database is not null && !Permissions.Has(http.User, Permissions.DatabasesManage)
            ? Results.Problem("This recipe creates a database, which your role can't do.", statusCode: StatusCodes.Status403Forbidden)
            : null;

    private static IResult Invalid(string message) => Results.ValidationProblem(new Dictionary<string, string[]> { ["recipe"] = [message] });
}
