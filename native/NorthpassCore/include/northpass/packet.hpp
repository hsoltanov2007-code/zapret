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
    std::uint32_t sequence{}, acknowledgement{};
    std::uint16_t fragment_offset{};
    std::uint32_t fragment_id{};
    bool more_fragments{}, atomic_fragment{};
    std::uint8_t extension_count{};
    TlsKind tls{TlsKind::Unknown};
    std::span<const std::uint8_t> payload{};
};
// Borrowed views last only until the input buffer is reused. Checksums may be
// offloaded: structural validation is not authenticity or checksum validation.
PacketView classify(std::span<const std::uint8_t> packet) noexcept;
TlsKind classify_tls(std::span<const std::uint8_t> payload) noexcept;
}
