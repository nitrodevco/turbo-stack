namespace TurboManager.Agent.Tests;

/// <summary>A temporary directory laid out like a host filesystem (etc/, proc/, run/).</summary>
public sealed class FakeHostRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tm-host-" + Guid.NewGuid().ToString("N"));

    public FakeHostRoot() => Directory.CreateDirectory(Path);

    public FakeHostRoot WithFile(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);

        return this;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
