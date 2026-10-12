using System.Text;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Deploy;

/// <summary>
/// SSH and git settings for an app user's repositories. Each deploy key gets a host alias
/// ("tm-&lt;key&gt;") in a private ssh config, and git URL rewrites send every submodule to the alias of
/// its own key (GitHub deploy keys only grant access to one repository each).
/// </summary>
public static class GitSsh
{
    public static string Alias(string keyName) => $"tm-{keyName}";

    public static string RemoteUrl(RepositoryAccess repository, string httpsBase) =>
        string.IsNullOrEmpty(repository.KeyName)
            ? $"{httpsBase.TrimEnd('/')}/{repository.Owner}/{repository.Name}.git"
            : $"{Alias(repository.KeyName)}:{repository.Owner}/{repository.Name}.git";

    public static string RenderConfig(IEnumerable<string> keyNames, string home, string host, int port)
    {
        var builder = new StringBuilder("# Managed by Turbo Manager: deploy keys for this user's repositories.\n");

        foreach (var key in keyNames.Distinct())
        {
            builder.Append($"\nHost {Alias(key)}\n")
                .Append($"    HostName {host}\n")
                .Append($"    Port {port}\n")
                .Append("    User git\n")
                .Append($"    IdentityFile {home}/.ssh/tm/{key}\n")
                .Append("    IdentitiesOnly yes\n")
                .Append($"    UserKnownHostsFile {home}/.ssh/tm/known_hosts\n")
                .Append("    StrictHostKeyChecking yes\n")
                .Append("    BatchMode yes\n");
        }

        return builder.ToString();
    }

    public static string RenderKnownHosts(string host, int port, IEnumerable<string> keys)
    {
        var name = port == 22 ? host : $"[{host}]:{port}";

        return string.Join('\n', keys.Select(k => $"{name} {k.Trim()}")) + "\n";
    }

    /// <summary>
    /// `git -c url.&lt;to&gt;.insteadOf=&lt;from&gt;` pairs so each submodule URL style (SSH, HTTPS, or relative
    /// to the main remote) resolves to the right key, or to HTTPS for public submodules.
    /// </summary>
    public static List<string> SubmoduleRewrites(GitSyncRequest request)
    {
        var arguments = new List<string>();
        var host = request.SshHost;
        var httpsHost = new Uri(request.HttpsBase).Host;

        foreach (var submodule in request.SubmoduleAccess)
        {
            var target = RemoteUrl(submodule, request.HttpsBase);
            var path = $"{submodule.Owner}/{submodule.Name}";
            string[] sources =
            [
                $"git@{host}:{path}.git",
                $"git@{host}:{path}",
                $"ssh://git@{host}/{path}.git",
                $"https://{httpsHost}/{path}.git",
                $"https://{httpsHost}/{path}",
            ];

            // Relative submodule URLs ("../sub.git") resolve against the main remote's alias.
            if (!string.IsNullOrEmpty(request.Repository.KeyName))
            {
                sources = [.. sources, $"{Alias(request.Repository.KeyName)}:{path}.git"];
            }

            foreach (var source in sources.Where(s => s != target))
            {
                arguments.Add("-c");
                arguments.Add($"url.{target}.insteadOf={source}");
            }
        }

        return arguments;
    }
}
