using System.Collections.Frozen;

namespace Northpass.Engine.Zapret1;

// Audited subset of v72.9 nfq/nfqws.c's long_options table, used by the five
// compiled definitions. No shell/config-file, auto-hostlist writing, Lua,
// daemon, service, raw filter, debug-file or executable-import switches.
public static class Zapret1Options
{
    public static readonly IReadOnlySet<string> Known = new[]
    {
        "--wf-tcp", "--wf-udp", "--filter-udp", "--filter-tcp", "--filter-l7", "--hostlist", "--hostlist-domains",
        "--hostlist-exclude", "--ipset", "--ipset-exclude", "--ip-id", "--dpi-desync", "--dpi-desync-repeats",
        "--dpi-desync-fake-quic", "--dpi-desync-fake-discord", "--dpi-desync-fake-stun", "--dpi-desync-split-seqovl",
        "--dpi-desync-split-pos", "--dpi-desync-split-seqovl-pattern", "--dpi-desync-any-protocol", "--dpi-desync-cutoff",
        "--dpi-desync-fake-unknown-udp", "--dpi-desync-fooling", "--dpi-desync-fakedsplit-pattern", "--dpi-desync-fake-tls",
        "--dpi-desync-fake-http", "--dpi-desync-fake-unknown", "--dpi-desync-hostfakesplit-mod", "--dpi-desync-fake-tls-mod"
    }.ToFrozenSet(StringComparer.Ordinal);
    public static bool ValidPorts(string? ports)
    {
        if (string.IsNullOrEmpty(ports) || ports.Length > 256) return false;
        var parts = ports.Split(','); if (parts.Length > 16) return false;
        foreach (string part in parts)
        {
            var range = part.Split('-');
            if (range.Length is < 1 or > 2 || range.Any(r => r.Length == 0 || r.Any(c => !char.IsAsciiDigit(c)) || !int.TryParse(r, out int p) || p is < 1 or > 65535)) return false;
            if (range.Length == 2 && int.Parse(range[0]) > int.Parse(range[1])) return false;
        }
        return true;
    }
}
