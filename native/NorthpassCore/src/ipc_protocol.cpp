#include "northpass/ipc_protocol.hpp"
#include <charconv>
#include <stdexcept>
namespace northpass {
namespace {
bool hex(std::string_view text, std::size_t size) noexcept {
    if (text.size() != size) return false;
    for (auto c : text) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
    return true;
}
}
bool valid_pipe_id(std::string_view id) noexcept { return hex(id, 32); }
bool valid_nonce(std::string_view nonce) noexcept { return hex(nonce, 64); }
bool authenticate(std::string_view message, std::string_view nonce) noexcept {
    if (!valid_nonce(nonce) || message.size() != 71 || message.substr(0, 7) != "AUTH 2 ") return false;
    unsigned difference = 0;
    for (std::size_t i = 0; i < 64; ++i) difference |= static_cast<unsigned char>(message[i + 7]) ^ static_cast<unsigned char>(nonce[i]);
    return difference == 0;
}
Command parse_command(std::string_view message, std::uint32_t expected) {
    if (message.size() > 64 || expected == 0) throw std::invalid_argument("IPC command limit exceeded.");
    const auto split = message.find(" 2 ");
    if (split == std::string_view::npos) throw std::invalid_argument("Unsupported IPC protocol.");
    CommandKind kind;
    const auto type = message.substr(0, split);
    if (type == "PING") kind = CommandKind::Ping;
    else if (type == "METRICS") kind = CommandKind::Metrics;
    else if (type == "STOP") kind = CommandKind::Stop;
    else throw std::invalid_argument("IPC command is not permitted.");
    const auto text = message.substr(split + 3); std::uint32_t sequence{};
    const auto result = std::from_chars(text.data(), text.data() + text.size(), sequence);
    if (text.empty() || text[0] == '0' || result.ec != std::errc{} || result.ptr != text.data() + text.size() || sequence != expected)
        throw std::invalid_argument("Invalid/replayed IPC sequence.");
    return {kind, sequence};
}
}
