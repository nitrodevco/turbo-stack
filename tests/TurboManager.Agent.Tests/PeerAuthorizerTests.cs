using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TurboManager.Agent.Security;
using TurboManager.Core.Unix;

namespace TurboManager.Agent.Tests;

public sealed class PeerAuthorizerTests : IDisposable
{
    private readonly FakeHostRoot _host = new FakeHostRoot().WithFile("etc/passwd",
        "root:x:0:0:root:/root:/bin/bash\n" +
        "turbo-stack:x:998:998::/var/lib/turbo-stack:/usr/sbin/nologin\n" +
        "hotel:x:1001:1001::/home/hotel:/bin/bash\n");

    [Theory]
    [InlineData(0, true)]
    [InlineData(998, true)]
    [InlineData(1001, false)]
    [InlineData(65534, false)]
    public void Allows_only_root_and_the_panel_user(int uid, bool allowed)
    {
        var authorizer = Create(allowUnverified: false);

        Assert.Equal(allowed, authorizer.IsAllowed(new PeerCredentials(1234, uid, uid)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Unknown_peers_are_rejected_unless_explicitly_allowed(bool allowUnverified, bool allowed)
    {
        Assert.Equal(allowed, Create(allowUnverified).IsAllowed(null));
    }

    [Fact]
    public void Passwd_parser_skips_comments_and_malformed_lines()
    {
        Assert.Null(PasswdFile.ParseLine("# comment"));
        Assert.Null(PasswdFile.ParseLine("broken:line"));
        Assert.Equal(new PasswdEntry("hotel", 1001, 1001, "/home/hotel", "/bin/bash"),
            PasswdFile.ParseLine("hotel:x:1001:1001::/home/hotel:/bin/bash"));
    }

    private PeerAuthorizer Create(bool allowUnverified) => new(
        Options.Create(new AgentOptions { HostRoot = _host.Path, AllowUnverifiedPeers = allowUnverified }),
        NullLogger<PeerAuthorizer>.Instance);

    public void Dispose() => _host.Dispose();
}
