using Microsoft.Extensions.Options;
using TurboManager.Agent.HostInfo;

namespace TurboManager.Agent.Tests;

public sealed class SystemInfoReaderTests : IDisposable
{
    private readonly FakeHostRoot _host = new FakeHostRoot()
        .WithFile("etc/hostname", "hotel-01\n")
        .WithFile("etc/os-release", "PRETTY_NAME=\"Ubuntu 24.04.3 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\n")
        .WithFile("proc/meminfo", "MemTotal:       16314200 kB\nMemFree:         1000000 kB\nMemAvailable:    8157100 kB\n")
        .WithFile("proc/uptime", "3600.55 7000.10\n")
        .WithFile("proc/loadavg", "0.42 0.30 0.25 1/300 12345\n")
        .WithFile("proc/sys/kernel/osrelease", "6.8.0-45-generic\n");

    [Fact]
    public void Reads_host_facts_from_proc_and_etc()
    {
        var info = CreateReader().Read();

        Assert.Equal("hotel-01", info.Hostname);
        Assert.Equal("Ubuntu 24.04.3 LTS", info.OsName);
        Assert.Equal("24.04", info.OsVersionId);
        Assert.Equal("6.8.0-45-generic", info.Kernel);
        Assert.Equal(3600, info.UptimeSeconds);
        Assert.Equal(16314200L * 1024, info.MemoryTotalBytes);
        Assert.Equal(8157100L * 1024, info.MemoryAvailableBytes);
        Assert.Equal(0.42, info.LoadAverage1M, 3);
        Assert.Equal("", info.Container);
    }

    [Fact]
    public void Detects_container_from_systemd_marker()
    {
        _host.WithFile("run/systemd/container", "docker\n");

        Assert.Equal("docker", CreateReader().DetectContainer());
    }

    [Fact]
    public void Detects_docker_from_dockerenv()
    {
        _host.WithFile(".dockerenv", "");

        Assert.Equal("docker", CreateReader().DetectContainer());
    }

    private SystemInfoReader CreateReader() => new(Options.Create(new AgentOptions { HostRoot = _host.Path }));

    public void Dispose() => _host.Dispose();
}
