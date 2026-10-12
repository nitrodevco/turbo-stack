using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TurboManager.Panel.Recipes;

public sealed class RecipeTemplateException(string message) : Exception(message);

/// <summary>
/// The small template language recipes use. <c>{{ path }}</c> inserts a value (input.x, port.x, secret.x,
/// db.x, app.x, site.x, user), optionally through filters, and optionally with a fallback or a choice:
/// <code>
/// {{ input.siteUrl | origin }}
/// {{ input.hotelName ?? 'Turbo' }}
/// {{ input.adminUrl ? 'true' : 'false' }}
/// </code>
/// Filters: origin (scheme://host[:port] of a URL), host, ident (a safe database name), json (a JSON string),
/// trimslash. There is deliberately nothing else: no loops, no code.
/// </summary>
public static partial class RecipeTemplate
{
    public sealed record Result(string Text, bool UsedEmptyValue);

    public static Result Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var usedEmpty = false;
        var text = Expression().Replace(template, match =>
        {
            var (value, guarded) = Evaluate(match.Groups["expr"].Value, values);

            if (value.Length == 0 && !guarded)
            {
                usedEmpty = true;
            }

            return value;
        });

        if (text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal))
        {
            throw new RecipeTemplateException($"Unbalanced braces in \"{template}\".");
        }

        return new Result(text, usedEmpty);
    }

    /// <summary>The value paths a template refers to (for validating manifests).</summary>
    public static IEnumerable<string> Paths(string template) =>
        Expression().Matches(template).Select(m => Parse(m.Groups["expr"].Value).Path);

    private static (string Value, bool Guarded) Evaluate(string expression, IReadOnlyDictionary<string, string> values)
    {
        var parsed = Parse(expression);

        if (!values.TryGetValue(parsed.Path, out var value))
        {
            throw new RecipeTemplateException($"Unknown value \"{parsed.Path}\".");
        }

        foreach (var filter in parsed.Filters)
        {
            value = Apply(filter, value);
        }

        if (parsed.Choice is { } choice)
        {
            return (value.Length > 0 && value != "false" ? choice.WhenSet : choice.WhenEmpty, true);
        }

        if (parsed.Fallback is { } fallback)
        {
            return (value.Length > 0 ? value : fallback, true);
        }

        return (value, false);
    }

    private sealed record Parsed(string Path, List<string> Filters, string? Fallback, (string WhenSet, string WhenEmpty)? Choice);

    private static Parsed Parse(string expression)
    {
        var match = ExpressionParts().Match(expression);

        if (!match.Success)
        {
            throw new RecipeTemplateException($"Can't read the template expression \"{{{{{expression}}}}}\".");
        }

        var filters = match.Groups["filter"].Captures.Select(c => c.Value).ToList();

        foreach (var filter in filters.Where(f => f is not ("origin" or "host" or "ident" or "json" or "trimslash")))
        {
            throw new RecipeTemplateException($"Unknown filter \"{filter}\".");
        }

        return new Parsed(
            match.Groups["path"].Value,
            filters,
            match.Groups["fallback"].Success ? match.Groups["fallback"].Value : null,
            match.Groups["set"].Success ? (match.Groups["set"].Value, match.Groups["empty"].Value) : null);
    }

    private static string Apply(string filter, string value)
    {
        if (value.Length == 0 && filter != "json")
        {
            return value;
        }

        switch (filter)
        {
            case "origin":
            case "host":
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ws" or "wss"))
                {
                    throw new RecipeTemplateException($"\"{value}\" isn't a full address (like https://example.com).");
                }

                return filter == "host" ? uri.Authority : $"{uri.Scheme}://{uri.Authority}";
            case "ident":
                var ident = new StringBuilder();

                foreach (var c in value.ToLowerInvariant())
                {
                    ident.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_');
                }

                return ident.ToString();
            case "json":
                return JsonSerializer.Serialize(value);
            default:
                return value.TrimEnd('/');
        }
    }

    [GeneratedRegex(@"\{\{(?<expr>[^{}]*)\}\}")]
    private static partial Regex Expression();

    [GeneratedRegex(@"^\s*(?<path>[a-zA-Z][a-zA-Z0-9_.]*)\s*(?:\|\s*(?<filter>[a-z]+)\s*)*(?:\?\?\s*'(?<fallback>[^']*)'\s*|\?\s*'(?<set>[^']*)'\s*:\s*'(?<empty>[^']*)'\s*)?$")]
    private static partial Regex ExpressionParts();
}
