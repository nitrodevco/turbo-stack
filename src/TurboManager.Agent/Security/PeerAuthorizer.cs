using Microsoft.Extensions.Options;
using TurboManager.Core.Unix;

namespace TurboManager.Agent.Security;

/// <summary>Decides whether a peer process may talk to the agent: root, or the panel's system user.</summary>
public sealed class PeerAuthorizer(IOptions<AgentOptions> options, ILogger<PeerAuthorizer> logger)
{
    private readonly Lazy<int?> _allowedUid = new(() =>
        PasswdFile.FindUser(options.Value.AllowedUser, Path.Combine(options.Value.HostRoot, "etc/passwd"))?.Uid);

    public bool IsAllowed(PeerCredentials? peer)
    {
        if (peer is null)
        {
            if (options.Value.AllowUnverifiedPeers)
            {
                return true;
            }

            logger.LogWarning("Rejected agent connection: peer credentials unavailable");

            return false;
        }

        var cred = peer.Value;

        if (cred.Uid == 0 || cred.Uid == _allowedUid.Value)
        {
            return true;
        }

        logger.LogWarning("Rejected agent connection from pid {Pid} uid {Uid}", cred.Pid, cred.Uid);

        return false;
    }
}
