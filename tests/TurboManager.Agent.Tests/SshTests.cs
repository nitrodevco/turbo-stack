using System.Buffers.Binary;
using System.Text;
using TurboManager.Agent.Ssh;

namespace TurboManager.Agent.Tests;

public sealed class SshTests
{
    public static string Ed25519Blob(byte fill = 7)
    {
        var type = Encoding.ASCII.GetBytes("ssh-ed25519");
        var blob = new byte[4 + type.Length + 4 + 32];
        BinaryPrimitives.WriteInt32BigEndian(blob, type.Length);
        type.CopyTo(blob, 4);
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(4 + type.Length), 32);
        blob.AsSpan(8 + type.Length).Fill(fill);

        return Convert.ToBase64String(blob);
    }

    [Fact]
    public void Valid_key_with_options_and_comment_is_parsed()
    {
        var line = $"from=\"10.0.0.1\",command=\"echo hi there\" ssh-ed25519 {Ed25519Blob()} bill@laptop";

        Assert.True(AuthorizedKeysParser.TryParse(line, out var key, out _));
        Assert.Equal("ssh-ed25519", key.Type);
        Assert.Equal("bill@laptop", key.Comment);
        Assert.Equal("from=\"10.0.0.1\",command=\"echo hi there\"", key.Options);
        Assert.StartsWith("SHA256:", key.Fingerprint);
        Assert.Equal(line, key.ToLine());
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("ssh-ed25519 !!!notbase64!!!")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP")]
    public void Invalid_keys_are_rejected(string line)
    {
        Assert.False(AuthorizedKeysParser.TryParse(line, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Key_whose_blob_type_differs_from_declared_type_is_rejected()
    {
        Assert.False(AuthorizedKeysParser.TryParse($"ssh-rsa {Ed25519Blob()}", out _, out var error));
        Assert.Contains("doesn't match", error);
    }

    [Fact]
    public void Fingerprint_matches_openssh_format()
    {
        // Same blob always yields the same fingerprint; different blobs differ.
        AuthorizedKeysParser.TryParse($"ssh-ed25519 {Ed25519Blob(1)}", out var a, out _);
        AuthorizedKeysParser.TryParse($"ssh-ed25519 {Ed25519Blob(1)} other-comment", out var b, out _);
        AuthorizedKeysParser.TryParse($"ssh-ed25519 {Ed25519Blob(2)}", out var c, out _);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(a.Fingerprint, c.Fingerprint);
        Assert.DoesNotContain("=", a.Fingerprint);
    }

    [Fact]
    public void Effective_sshd_config_is_parsed()
    {
        var settings = SshdConfig.ParseEffective("port 22\nport 2222\npasswordauthentication no\npermitrootlogin without-password\npubkeyauthentication yes\n");

        Assert.False(settings.PasswordAuthentication);
        Assert.Equal("prohibit-password", settings.PermitRootLogin);
        Assert.True(settings.PubkeyAuthentication);
        Assert.Equal([22, 2222], settings.Ports);
    }

    [Fact]
    public void Drop_in_round_trips_only_the_values_that_were_set()
    {
        var content = SshdConfig.RenderDropIn(false, null, "Turbo Stack");

        Assert.Contains("PasswordAuthentication no", content);
        Assert.Contains("KbdInteractiveAuthentication no", content);
        Assert.DoesNotContain("PermitRootLogin", content);
        Assert.Equal((false, (string?)null), SshdConfig.ParseDropIn(content));
    }
}
