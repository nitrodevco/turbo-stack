using Microsoft.AspNetCore.Authorization;
using TurboManager.Core;
using TurboManager.Panel.Auth;

namespace TurboManager.Panel.Http;

/// <summary>
/// The panel's OpenAPI document (/api/openapi.json, for signed-in users and API tokens): every endpoint with the
/// permission it needs and whether it asks for the second factor again. The panel's API page renders it.
/// </summary>
public static class ApiDocs
{
    public const string Path = "/api/openapi.json";

    public static IServiceCollection AddApiDocs(this IServiceCollection services) =>
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = $"{Branding.ProductName} API";
                document.Info.Version = ReleaseVersion.Installed();
                document.Info.Description =
                    "Authenticate with an API token from your Account page: `Authorization: Bearer tm_…`. A token can only do what it "
                    + "was given (and what its owner's role allows). Endpoints marked *second factor* are for signed-in sessions only.";

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var permissions = metadata.OfType<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null && Permissions.All.Contains(p)).Distinct().ToList();
                var notes = new List<string>();

                if (permissions.Count > 0)
                {
                    notes.Add($"Permission: `{string.Join("`, `", permissions)}`.");
                }

                if (metadata.OfType<SudoRequired>().Any())
                {
                    notes.Add("Needs the second factor again (sudo mode): sessions only, never API tokens.");
                }

                if (metadata.OfType<IAllowAnonymous>().Any())
                {
                    notes.Add("No sign-in needed.");
                }

                if (notes.Count > 0)
                {
                    operation.Description = string.Join(' ', notes);
                }

                return Task.CompletedTask;
            });
        });

    public static void MapApiDocs(this WebApplication app) =>
        app.MapOpenApi(Path).RequireAuthorization(policy => policy.AddAuthenticationSchemes(AuthSchemes.Session, AuthSchemes.Token).RequireAuthenticatedUser());
}
