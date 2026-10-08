#pragma once
#include <span>
#include <string>
#include <string_view>
#include <cstdint>
namespace northpass {
struct Options {
    bool version{}, check{}, loopback{};
    std::uint16_t port{};
    std::uint32_t parent_pid{};
    std::string protocol{"both"};
    std::string pipe_id{};
    std::string filter() const;
};
Options parse_options(std::span<const std::string_view> args);
}
