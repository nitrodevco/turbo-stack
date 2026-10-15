namespace TurboManager.Panel.Cloudflare;

public enum DnsActionKind
{
    /// <summary>Record already correct.</summary>
    Ok,
    Create,
    Update,

    /// <summary>A record exists that the panel won't overwrite without permission (CNAME, or a different A/AAAA).</summary>
    Conflict,
    Delete,
}

public sealed record DnsAction(DnsActionKind Kind, string Type, string Name, string? Content, bool Proxied, CloudflareDnsRecord? Existing, string? Reason = null);

/// <summary>Decides how to make Cloudflare's records for one name point at this server.</summary>
public static class CloudflareDnsPlanner
{
    public const string Comment = "Managed by Turbo Stack";

    /// <param name="desired">Desired A/AAAA content by type, e.g. {"A": "203.0.113.10"}.</param>
    /// <param name="replaceConflicts">Overwrite records pointing elsewhere (and CNAMEs) instead of reporting them.</param>
    public static List<DnsAction> Plan(string name, IReadOnlyDictionary<string, string> desired, bool proxied,
        IReadOnlyList<CloudflareDnsRecord> existing, bool replaceConflicts)
    {
        var actions = new List<DnsAction>();
        var relevant = existing.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && r.Type is "A" or "AAAA" or "CNAME").ToList();
        var cname = relevant.FirstOrDefault(r => r.Type == "CNAME");

        if (cname is not null)
        {
            if (!replaceConflicts)
            {
                return [new DnsAction(DnsActionKind.Conflict, "CNAME", name, cname.Content, cname.Proxied, cname, $"{name} is a CNAME to {cname.Content}.")];
            }

            // A CNAME can't coexist with A/AAAA records on the same name.
            actions.Add(new DnsAction(DnsActionKind.Delete, "CNAME", name, cname.Content, cname.Proxied, cname));
        }

        foreach (var (type, content) in desired)
        {
            var records = relevant.Where(r => r.Type == type).ToList();
            var match = records.FirstOrDefault(r => r.Content == content);

            if (match is not null)
            {
                actions.Add(match.Proxied == proxied
                    ? new DnsAction(DnsActionKind.Ok, type, name, content, proxied, match)
                    : new DnsAction(DnsActionKind.Update, type, name, content, proxied, match, proxied ? "Turn on proxying." : "Turn off proxying."));
            }
            else if (records.Count == 0)
            {
                actions.Add(new DnsAction(DnsActionKind.Create, type, name, content, proxied, null));
            }
            else if (replaceConflicts)
            {
                actions.Add(new DnsAction(DnsActionKind.Update, type, name, content, proxied, records[0], $"Was {records[0].Content}."));
                actions.AddRange(records.Skip(1).Select(r => new DnsAction(DnsActionKind.Delete, type, name, r.Content, r.Proxied, r)));
                continue;
            }
            else
            {
                actions.Add(new DnsAction(DnsActionKind.Conflict, type, name, content, proxied, records[0],
                    $"{name} points to {string.Join(", ", records.Select(r => r.Content))}, not this server."));
                continue;
            }

            // Extra records of the same type would round-robin traffic away from this server.
            actions.AddRange(records.Where(r => r != match).Select(r => replaceConflicts
                ? new DnsAction(DnsActionKind.Delete, type, name, r.Content, r.Proxied, r)
                : new DnsAction(DnsActionKind.Conflict, type, name, r.Content, r.Proxied, r, $"Another {type} record points to {r.Content}.")));
        }

        return actions;
    }

    /// <summary>The zone that hosts <paramref name="domain"/>: the longest zone name it ends with.</summary>
    public static T? ZoneFor<T>(IEnumerable<T> zones, Func<T, string> zoneName, string domain) where T : class =>
        zones
            .Where(z => domain.Equals(zoneName(z), StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + zoneName(z), StringComparison.OrdinalIgnoreCase))
            .MaxBy(z => zoneName(z).Length);
}
