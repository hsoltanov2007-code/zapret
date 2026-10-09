#pragma once
#include <cstdint>
#include <string_view>
namespace northpass {
constexpr std::size_t MaximumIpcMessage = 2048;
enum class CommandKind { Ping, Metrics, Stop };
struct Command { CommandKind kind; std::uint32_t sequence; };
Command parse_command(std::string_view message, std::uint32_t expected_sequence);
bool valid_pipe_id(std::string_view id) noexcept;
bool valid_nonce(std::string_view nonce) noexcept;
bool authenticate(std::string_view message, std::string_view nonce) noexcept;
}
