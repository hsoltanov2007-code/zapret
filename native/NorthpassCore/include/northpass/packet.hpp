#pragma once
#include <array>
#include <compare>
#include <cstdint>
#include <span>

namespace northpass {
enum class ParseState { Parsed, Malformed, Fragment, Unsupported };
enum class Transport { Other, Tcp = 6, Udp = 17 };
enum class TlsKind { Unknown, RecordFraming, ClientHelloFraming };
struct Endpoint {
    std::array<std::uint8_t, 16> address{};
    std::uint16_t port{};
    auto operator<=>(const Endpoint&) const = default;
};
struct PacketView {
    ParseState state{ParseState::Malformed};
    std::uint8_t ip_version{};
    Transport transport{Transport::Other};
    Endpoint source{}, destination{};
    std::uint8_t tcp_flags{};
    TlsKind tls{TlsKind::Unknown};
    std::span<const std::uint8_t> payload{};
};
// Observational parsing only; even malformed/unknown packets are forwarded intact.
PacketView classify(std::span<const std::uint8_t> packet) noexcept;
TlsKind classify_tls(std::span<const std::uint8_t> payload) noexcept;
}
