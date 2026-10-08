#include "northpass/packet.hpp"
#include <algorithm>
#include <utility>

namespace northpass {
namespace {
std::uint16_t u16(std::span<const std::uint8_t> data, std::size_t at) noexcept {
    return static_cast<std::uint16_t>((static_cast<unsigned>(data[at]) << 8) | data[at + 1]);
}
std::uint32_t u32(std::span<const std::uint8_t> data, std::size_t at) noexcept {
    return (static_cast<std::uint32_t>(u16(data, at)) << 16) | u16(data, at + 2);
}
bool options_valid(std::span<const std::uint8_t> options) noexcept {
    for (std::size_t i = 0; i < options.size();) {
        if (options[i] == 0) return true;
        if (options[i] == 1) { ++i; continue; }
        if (options.size() - i < 2 || options[i + 1] < 2 || options[i + 1] > options.size() - i) return false;
        i += options[i + 1];
    }
    return true;
}
bool ipv6_options_valid(std::span<const std::uint8_t> data) noexcept {
    for (std::size_t i = 2; i < data.size();) {
        if (data[i] == 0) { ++i; continue; }
        if (data.size() - i < 2 || data[i + 1] > data.size() - i - 2) return false;
        i += static_cast<std::size_t>(data[i + 1]) + 2;
    }
    return true;
}
}
TlsKind classify_tls(std::span<const std::uint8_t> data) noexcept {
    if (data.size() < 5 || data[0] < 20 || data[0] > 23 || data[1] != 3 || data[2] < 1 || data[2] > 3) return TlsKind::Unknown;
    const auto length = u16(data, 3);
    if (length == 0 || length > 18432 || length > data.size() - 5) return TlsKind::Unknown;
    // A framing observation is not cryptographic authentication or stream reassembly.
    if (data[0] == 22 && length >= 4 && data[5] == 1) {
        const auto hello_length = (static_cast<std::size_t>(data[6]) << 16) | (static_cast<std::size_t>(data[7]) << 8) | data[8];
        if (hello_length >= 34 && hello_length <= static_cast<std::size_t>(length - 4)) return TlsKind::ClientHelloFraming;
    }
    return TlsKind::RecordFraming;
}
PacketView classify(std::span<const std::uint8_t> data) noexcept {
    PacketView view;
    if (data.empty()) return view;
    view.ip_version = data[0] >> 4;
    std::size_t offset{}, end{};
    std::uint8_t protocol{};
    if (view.ip_version == 4) {
        if (data.size() < 20) return view;
        offset = static_cast<std::size_t>(data[0] & 15) * 4;
        end = u16(data, 2);
        if (offset < 20 || offset > end || end != data.size() || data[8] == 0 || !options_valid(data.subspan(20, offset - 20))) return view;
        std::copy_n(data.begin() + 12, 4, view.source.address.begin() + 12);
        std::copy_n(data.begin() + 16, 4, view.destination.address.begin() + 12);
        const auto fragment = u16(data, 6);
        if (fragment & 0x8000) return view; // reserved flag
        view.fragment_offset = static_cast<std::uint16_t>((fragment & 0x1fff) * 8);
        view.more_fragments = (fragment & 0x2000) != 0; view.fragment_id = u16(data, 4);
        if ((fragment & 0x3fff) != 0) {
            if ((fragment & 0x4000) || end == offset || (view.more_fragments && (end - offset) % 8 != 0) ||
                static_cast<std::size_t>(view.fragment_offset) + end - offset > 65535) return view;
            view.state = ParseState::Fragment; return view;
        }
        protocol = data[9];
    } else if (view.ip_version == 6) {
        if (data.size() < 40) return view;
        const auto length = u16(data, 4);
        if (length == 0) { view.state = ParseState::Unsupported; return view; } // no jumbo payload support
        end = 40 + static_cast<std::size_t>(length); offset = 40;
        if (end != data.size() || data[7] == 0) return view;
        std::copy_n(data.begin() + 8, 16, view.source.address.begin());
        std::copy_n(data.begin() + 24, 16, view.destination.address.begin());
        protocol = data[6];
        bool fragment_seen = false, routing_seen = false, ah_seen = false;
        unsigned destinations = 0;
        while (protocol == 0 || protocol == 43 || protocol == 60 || protocol == 51 || protocol == 44) {
            if (++view.extension_count > 8) { view.state = ParseState::Unsupported; return view; }
            if (end - offset < 8) return view;
            if (protocol == 0 && offset != 40) return view;
            if (protocol == 43 && std::exchange(routing_seen, true)) return view;
            if (protocol == 51 && std::exchange(ah_seen, true)) return view;
            if (protocol == 60 && ++destinations > 2) return view;
            if (protocol == 44) {
                if (fragment_seen || data[offset + 1] != 0 || (u16(data, offset + 2) & 6) != 0) return view;
                fragment_seen = true;
                view.fragment_offset = static_cast<std::uint16_t>(u16(data, offset + 2) & 0xfff8);
                view.more_fragments = (data[offset + 3] & 1) != 0; view.fragment_id = u32(data, offset + 4);
                if (view.fragment_offset != 0 || view.more_fragments) {
                    if (end == offset + 8 || (view.more_fragments && (end - offset - 8) % 8 != 0) ||
                        static_cast<std::size_t>(view.fragment_offset) + end - offset - 8 > 65535) return view;
                    view.state = ParseState::Fragment; return view;
                }
                view.atomic_fragment = true; // RFC 6946: parse independent atomic fragment, never reconstruct.
                protocol = data[offset]; offset += 8; continue;
            }
            const auto extension_length = (static_cast<std::size_t>(data[offset + 1]) + (protocol == 51 ? 2 : 1)) * (protocol == 51 ? 4 : 8);
            if (extension_length < 8 || extension_length > end - offset) return view;
            if ((protocol == 0 || protocol == 60) && !ipv6_options_valid(data.subspan(offset, extension_length))) return view;
            if (protocol == 51 && (extension_length < 12 || data[offset + 2] != 0 || data[offset + 3] != 0)) return view;
            protocol = data[offset]; offset += extension_length;
        }
    } else { view.state = ParseState::Unsupported; return view; }
    if (protocol == 6) {
        if (end - offset < 20) return view;
        const auto header = static_cast<std::size_t>(data[offset + 12] >> 4) * 4;
        if (header < 20 || header > end - offset || (data[offset + 12] & 0x0e) != 0 || !options_valid(data.subspan(offset + 20, header - 20))) return view;
        view.transport = Transport::Tcp; view.tcp_flags = data[offset + 13];
        view.sequence = u32(data, offset + 4); view.acknowledgement = u32(data, offset + 8);
        view.payload = data.subspan(offset + header, end - offset - header);
        view.tls = classify_tls(view.payload);
    } else if (protocol == 17) {
        if (end - offset < 8) return view;
        const auto length = u16(data, offset + 4);
        if (length < 8 || length != end - offset) return view;
        view.transport = Transport::Udp;
        view.payload = data.subspan(offset + 8, length - 8);
    } else { view.state = ParseState::Unsupported; return view; }
    view.source.port = u16(data, offset); view.destination.port = u16(data, offset + 2);
    view.state = ParseState::Parsed;
    return view;
}
}
