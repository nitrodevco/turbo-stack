namespace TurboManager.Core;

/// <summary>Release version numbers (scripts/package.sh: 1.2.3 or 1.2.3-beta.4).</summary>
public static class ReleaseVersion
{
    /// <summary>Semantic-version order: numeric parts, then a release sorts after its pre-releases (1.2.0 &gt; 1.2.0-beta.2).</summary>
    public static int Compare(string a, string b)
    {
        static (int[] Numbers, string Pre) Split(string version)
        {
            var core = version.Split('+')[0];
            var dash = core.IndexOf('-');
            var pre = dash < 0 ? "" : core[(dash + 1)..];
            var numbers = (dash < 0 ? core : core[..dash]).Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();

            return (numbers, pre);
        }

        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);

        for (var i = 0; i < Math.Max(na.Length, nb.Length); i++)
        {
            var compare = na.ElementAtOrDefault(i).CompareTo(nb.ElementAtOrDefault(i));

            if (compare != 0)
            {
                return compare;
            }
        }

        if (pa.Length == 0 || pb.Length == 0)
        {
            return (pa.Length == 0 ? 1 : 0) - (pb.Length == 0 ? 1 : 0);
        }

        var ia = pa.Split('.');
        var ib = pb.Split('.');

        for (var i = 0; i < Math.Max(ia.Length, ib.Length); i++)
        {
            if (i >= ia.Length || i >= ib.Length)
            {
                return ia.Length.CompareTo(ib.Length);
            }

            var compare = int.TryParse(ia[i], out var x) && int.TryParse(ib[i], out var y) ? x.CompareTo(y) : string.CompareOrdinal(ia[i], ib[i]);

            if (compare != 0)
            {
                return Math.Sign(compare);
            }
        }

        return 0;
    }

    /// <summary>The installed release (/opt/turbo-stack/VERSION), or this assembly's version outside an install.</summary>
    public static string Installed(string versionFile = $"{ManagerPaths.InstallRoot}/VERSION")
    {
        try
        {
            if (File.Exists(versionFile) && File.ReadAllText(versionFile).Trim() is { Length: > 0 } version)
            {
                return version;
            }
        }
        catch (IOException)
        {
        }

        return typeof(ReleaseVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
