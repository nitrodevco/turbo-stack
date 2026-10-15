using Microsoft.Extensions.Options;

namespace TurboManager.Agent.Firewall;

/// <summary>The files that fully describe ufw's configuration; snapshotting them makes any change revertible.</summary>
public sealed class UfwFiles(IOptions<AgentOptions> options)
{
    public string UserRules => HostPath("etc/ufw/user.rules");

    public string User6Rules => HostPath("etc/ufw/user6.rules");

    public string Defaults => HostPath("etc/default/ufw");

    public string Config => HostPath("etc/ufw/ufw.conf");

    public IReadOnlyList<string> All => [UserRules, User6Rules, Defaults, Config];

    public string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    /// <summary>Contents of every config file (null for files that don't exist).</summary>
    public Dictionary<string, string?> Snapshot() => All.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllText(path) : null);

    public void Restore(Dictionary<string, string?> snapshot)
    {
        foreach (var (path, content) in snapshot)
        {
            if (content is null)
            {
                File.Delete(path);

                continue;
            }

            var temp = path + ".ts-restore";
            File.WriteAllText(temp, content);

            if (!OperatingSystem.IsWindows() && File.Exists(path))
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            }

            File.Move(temp, path, overwrite: true);
        }
    }

    private string HostPath(string relativePath) => Path.Combine(options.Value.HostRoot, relativePath);
}
