#include "northpass/options.hpp"
#include <charconv>
#include <set>
#include <stdexcept>
namespace northpass {
namespace {
std::uint32_t number(std::string_view text) {
    std::uint32_t result{};
    const auto conversion = std::from_chars(text.data(), text.data() + text.size(), result);
    if (text.empty() || conversion.ec != std::errc{} || conversion.ptr != text.data() + text.size()) throw std::invalid_argument("Invalid unsigned decimal option.");
    return result;
}
}
Options parse_options(std::span<const std::string_view> args) {
    Options options;
    if (args.size() == 1 && args[0] == "--version") { options.version = true; return options; }
    std::set<std::string_view> seen;
    bool mode = false, control = false;
    for (std::size_t i = 0; i < args.size(); ++i) {
        const auto key = args[i];
        if (!seen.insert(key).second) throw std::invalid_argument("Duplicate option.");
        if (key == "--check") { options.check = true; continue; }
        if (key == "--stdio-control") { control = true; continue; }
        if (++i == args.size()) throw std::invalid_argument("Missing option value.");
        const auto value = args[i];
        if (key == "--mode") { mode = true; if (value != "idle" && value != "loopback") throw std::invalid_argument("Only idle/loopback modes exist in v0.1."); options.loopback = value == "loopback"; }
        else if (key == "--port") { const auto port = number(value); if (port < 49152 || port > 65535) throw std::invalid_argument("Only dedicated ephemeral loopback ports 49152..65535 are allowed."); options.port = static_cast<std::uint16_t>(port); }
        else if (key == "--parent-pid") options.parent_pid = number(value);
        else if (key == "--protocol") { if (value != "tcp" && value != "udp" && value != "both") throw std::invalid_argument("Invalid transport scope."); options.protocol = value; }
        else throw std::invalid_argument("Unknown option; arbitrary filters/strategies are forbidden.");
    }
    if (!mode || options.loopback != (options.port != 0) || (!options.loopback && seen.contains("--protocol"))) throw std::invalid_argument("Invalid mode/port combination.");
    if (!options.check && (!control || options.parent_pid == 0)) throw std::invalid_argument("Owned parent and stdin control are required.");
    return options;
}
std::string Options::filter() const {
    if (!loopback) return "false";
    const auto port_text = std::to_string(port);
    const auto tcp = "(tcp and (tcp.SrcPort == " + port_text + " or tcp.DstPort == " + port_text + "))";
    const auto udp = "(udp and (udp.SrcPort == " + port_text + " or udp.DstPort == " + port_text + "))";
    return "outbound and loopback and !impostor and ((ip and ip.SrcAddr == 127.0.0.1 and ip.DstAddr == 127.0.0.1) or (ipv6 and ipv6.SrcAddr == ::1 and ipv6.DstAddr == ::1)) and " +
        (protocol == "tcp" ? tcp : protocol == "udp" ? udp : "(" + tcp + " or " + udp + ")");
}
}
