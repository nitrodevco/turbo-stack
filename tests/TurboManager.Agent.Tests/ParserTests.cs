using TurboManager.Agent.Firewall;
using TurboManager.Agent.Journal;
using TurboManager.Agent.Metrics;
using TurboManager.Agent.Packages;
using TurboManager.Agent.Systemd;
using TurboManager.Contracts.Agent;

namespace TurboManager.Agent.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Systemctl_show_blocks_map_to_units()
    {
        const string output = """
            Id=nginx.service
            Description=A high performance web server
            LoadState=loaded
            ActiveState=active
            SubState=running
            UnitFileState=enabled
            MainPID=812
            MemoryCurrent=7340032
            CPUUsageNSec=1250000000
            ActiveEnterTimestamp=@1760155200
            NRestarts=2

            Id=ts-agent.service
            Description=Turbo Stack agent
            LoadState=loaded
            ActiveState=inactive
            SubState=dead
            UnitFileState=enabled
            MainPID=0
            MemoryCurrent=[not set]
            CPUUsageNSec=18446744073709551615
            ActiveEnterTimestamp=
            NRestarts=0
            """;

        var units = SystemctlParser.ParseShowBlocks(output).Select(SystemctlParser.ToServiceUnit).ToList();

        Assert.Equal(2, units.Count);
        Assert.Equal("nginx.service", units[0].Name);
        Assert.Equal(7340032, units[0].MemoryBytes);
        Assert.Equal(1_760_155_200_000, units[0].ActiveSinceUnixMs);
        Assert.Equal(2, units[0].Restarts);
        Assert.Equal(-1, units[1].MemoryBytes);
        Assert.Equal(-1, units[1].CpuUsageNsec);
        Assert.Equal(0, units[1].ActiveSinceUnixMs);
    }

    [Fact]
    public void Unit_names_skip_templates_and_non_services()
    {
        const string output = "nginx.service loaded active running nginx\ngetty@.service static -\nssh.socket enabled enabled\n";

        Assert.Equal(["nginx.service"], SystemctlParser.ParseUnitNames(output));
    }

    [Fact]
    public void Journal_json_line_is_parsed_including_byte_array_messages()
    {
        var entry = JournalParser.ParseLine("""{"__REALTIME_TIMESTAMP":"1760155200000000","_SYSTEMD_UNIT":"nginx.service","SYSLOG_IDENTIFIER":"nginx","PRIORITY":"3","MESSAGE":"failed","_PID":"812","__CURSOR":"s=abc"}""");
        var binary = JournalParser.ParseLine("""{"__REALTIME_TIMESTAMP":"1","MESSAGE":[104,105]}""");

        Assert.NotNull(entry);
        Assert.Equal("nginx.service", entry.Unit);
        Assert.Equal(3, entry.Priority);
        Assert.Equal(812, entry.Pid);
        Assert.Equal("hi", binary!.Message);
        Assert.Equal(6, binary.Priority);
        Assert.Null(JournalParser.ParseLine("-- No entries --"));
    }

    [Fact]
    public void Journal_arguments_include_filters()
    {
        var query = new JournalQuery { Unit = "nginx.service", MaxPriority = 3, Grep = "error", Limit = 50, SinceUnixMs = 1_760_155_200_000 };

        var arguments = JournalParser.BuildArguments(query, follow: true);

        Assert.Contains("--follow", arguments);
        Assert.Contains("--unit=nginx.service", arguments);
        Assert.Contains("--priority=0..3", arguments);
        Assert.Contains("--grep=error", arguments);
        Assert.Contains("--lines=50", arguments);
        Assert.Contains("--since=@1760155200", arguments);
    }

    [Fact]
    public void Cpu_percent_is_computed_from_two_samples()
    {
        var first = ProcReader.ParseCpuTimes("cpu  100 0 100 700 100 0 0 0 0 0\ncpu0 1 2 3");
        var second = ProcReader.ParseCpuTimes("cpu  200 0 200 1250 150 0 0 0 0 0\n");

        // total delta 800, idle+iowait delta 600 → 25% busy.
        Assert.Equal(25, ProcReader.CpuPercent(first, second), 3);
    }

    [Fact]
    public void Net_counters_skip_loopback()
    {
        const string netDev = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo: 5000 10 0 0 0 0 0 0 5000 10 0 0 0 0 0 0
              eth0: 1000 5 0 0 0 0 0 0 2000 7 0 0 0 0 0 0
              eth1: 10 1 0 0 0 0 0 0 20 1 0 0 0 0 0 0
            """;

        Assert.Equal(new NetCounters(1010, 2020), ProcReader.ParseNetCounters(netDev));
    }

    [Fact]
    public void Disk_mounts_include_root_and_real_filesystems_only()
    {
        const string mounts = """
            overlay / overlay rw 0 0
            proc /proc proc rw 0 0
            /dev/sda2 /data ext4 rw 0 0
            /dev/loop0 /snap/core/1 squashfs ro 0 0
            tmpfs /run tmpfs rw 0 0
            """;

        Assert.Equal(["/", "/data"], ProcReader.ParseDiskMounts(mounts).Select(m => m.Mount));
    }

    [Fact]
    public void Ufw_rule_files_are_parsed_with_comments_and_app_rules()
    {
        const string rules = """
            *filter
            ### RULES ###

            ### tuple ### limit tcp 22 0.0.0.0/0 any 0.0.0.0/0 in comment=74733a737368
            -A ufw-user-input -p tcp --dport 22 -j ufw-user-limit
            ### tuple ### allow tcp 80,443 0.0.0.0/0 any 203.0.113.0/24 in
            ### tuple ### allow_log any 22 0.0.0.0/0 any 0.0.0.0/0 OpenSSH - in_eth0
            ### tuple ### deny udp 6000:6010 0.0.0.0/0 any 198.51.100.7/32 in comment=74733a7231

            ### END RULES ###
            """;

        var parsed = UfwParser.ParseRulesFile(rules, ipv6: false);

        Assert.Equal(4, parsed.Count);
        Assert.Equal(("limit", "tcp", "22", "any", "ts:ssh"), (parsed[0].Action, parsed[0].Protocol, parsed[0].Ports, parsed[0].Source, parsed[0].Comment));
        Assert.Equal(("80,443", "203.0.113.0/24", ""), (parsed[1].Ports, parsed[1].Source, parsed[1].Comment));
        Assert.Equal(("allow", "in"), (parsed[2].Action, parsed[2].Direction));
        Assert.Equal(("198.51.100.7", "ts:r1"), (parsed[3].Source, parsed[3].Comment));
    }

    [Fact]
    public void Ufw_defaults_map_iptables_policies()
    {
        const string defaults = "IPV6=yes\nDEFAULT_INPUT_POLICY=\"DROP\"\nDEFAULT_OUTPUT_POLICY=\"ACCEPT\"\n";

        Assert.Equal(("deny", "allow"), UfwParser.ParseDefaults(defaults));
    }

    [Fact]
    public void Ss_output_is_parsed_and_deduplicated()
    {
        const string output = """
            udp   UNCONN 0      0      127.0.0.53%lo:53         0.0.0.0:*    users:(("systemd-resolve",pid=401,fd=13))
            tcp   LISTEN 0      511          0.0.0.0:80         0.0.0.0:*    users:(("nginx",pid=812,fd=6),("nginx",pid=813,fd=6))
            tcp   LISTEN 0      4096            [::]:22            [::]:*    users:(("sshd",pid=600,fd=4))
            tcp   LISTEN 0      512                *:9001             *:*    users:(("dotnet",pid=900,fd=200))
            tcp   LISTEN 0      512   [::ffff:127.0.0.1]:7800           *:*    users:(("ts-panel",pid=950,fd=10))
            tcp   LISTEN 0      511          0.0.0.0:80         0.0.0.0:*    users:(("nginx",pid=812,fd=7))
            """;

        var sockets = SsParser.Parse(output);

        Assert.Equal(5, sockets.Count);
        Assert.Contains(sockets, s => s is { Protocol: "udp", Address: "127.0.0.53", Port: 53, Process: "systemd-resolve" });
        Assert.Contains(sockets, s => s is { Address: "::", Port: 22, Process: "sshd", Pid: 600 });
        Assert.Contains(sockets, s => s is { Address: "0.0.0.0", Port: 9001 });
        Assert.Contains(sockets, s => s is { Address: "127.0.0.1", Port: 7800 });
    }

    [Fact]
    public void Fail2ban_status_output_is_parsed()
    {
        const string overview = "Status\n|- Number of jail:\t2\n`- Jail list:\tsshd, turbo-stack-panel\n";
        const string jail = """
            Status for the jail: sshd
            |- Filter
            |  |- Currently failed:	3
            |  |- Total failed:	42
            |  `- Journal matches:	_SYSTEMD_UNIT=sshd.service + _COMM=sshd
            `- Actions
               |- Currently banned:	2
               |- Total banned:	9
               `- Banned IP list:	203.0.113.9 198.51.100.4
            """;
        const string ignore = "These IP addresses/networks are ignored:\n|- 127.0.0.1/8\n`- ::1\n";

        Assert.Equal(["sshd", "turbo-stack-panel"], Fail2banParser.ParseJailList(overview));
        Assert.Equal(3, Fail2banParser.ReadInt(jail, "Currently failed"));
        Assert.Equal(9, Fail2banParser.ReadInt(jail, "Total banned"));
        Assert.Equal(["203.0.113.9", "198.51.100.4"], Fail2banParser.ParseBannedIps(jail));
        Assert.Equal(["127.0.0.1/8", "::1"], Fail2banParser.ParseIgnoreIps(ignore));
        Assert.Empty(Fail2banParser.ParseIgnoreIps("No IP address/network is ignored"));
    }

    [Fact]
    public void Ini_document_updates_one_section_and_keeps_others()
    {
        var document = IniDocument.Parse("# header\n[DEFAULT]\nbackend = systemd\n\n[sshd]\nenabled = true\nmaxretry = 5\n");

        document.Set("sshd", "maxretry", "3");
        document.Set("recidive", "enabled", "true");
        var reparsed = IniDocument.Parse(document.Write("Managed"));

        Assert.Equal("systemd", reparsed.Get("DEFAULT", "backend"));
        Assert.Equal("true", reparsed.Get("sshd", "enabled"));
        Assert.Equal("3", reparsed.Get("sshd", "maxretry"));
        Assert.Equal("true", reparsed.Get("recidive", "enabled"));
    }

    [Fact]
    public void Apt_upgradable_list_is_parsed_with_security_flag()
    {
        const string output = """
            Listing...
            libssl3t64/noble-updates,noble-security 3.0.13-0ubuntu3.5 amd64 [upgradable from: 3.0.13-0ubuntu3.4]
            nginx/noble-updates 1.24.0-2ubuntu7.2 amd64 [upgradable from: 1.24.0-2ubuntu7.1]
            """;

        var packages = AptParser.ParseUpgradable(output);

        Assert.Equal(2, packages.Count);
        Assert.True(packages[0].Security);
        Assert.Equal("3.0.13-0ubuntu3.4", packages[0].CurrentVersion);
        Assert.False(packages[1].Security);
        Assert.True(AptParser.IsValidPackageName("libssl3t64"));
        Assert.False(AptParser.IsValidPackageName("nginx; rm -rf /"));
    }

    [Fact]
    public void Scheduled_shutdown_file_is_parsed()
    {
        Assert.Equal(1_760_155_200_000, AptParser.ParseScheduledShutdownMs("USEC=1760155200000000\nWARN_WALL=1\nMODE=reboot\n"));
        Assert.Null(AptParser.ParseScheduledShutdownMs(""));
    }
}
