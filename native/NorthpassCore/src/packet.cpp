#include "northpass/packet.hpp"
#include <algorithm>

namespace northpass {
namespace {
std::uint16_t u16(std::span<const std::uint8_t> data, std::size_t at) noexcept {
    return static_cast<std::uint16_t>((static_cast<unsigned>(data[at]) << 8) | data[at + 1]);
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
        if (offset < 20 || offset > end || end > data.size()) return view;
        std::copy_n(data.begin() + 12, 4, view.source.address.begin() + 12);
        std::copy_n(data.begin() + 16, 4, view.destination.address.begin() + 12);
        if ((u16(data, 6) & 0x3fff) != 0) { view.state = ParseState::Fragment; return view; }
        protocol = data[9];
    } else if (view.ip_version == 6) {
        if (data.size() < 40) return view;
        const auto length = u16(data, 4);
        if (length == 0) { view.state = ParseState::Unsupported; return view; } // no jumbo payload support
        end = 40 + static_cast<std::size_t>(length); offset = 40;
        if (end > data.size()) return view;
        std::copy_n(data.begin() + 8, 16, view.source.address.begin());
        std::copy_n(data.begin() + 24, 16, view.destination.address.begin());
        protocol = data[6];
        unsigned extensions = 0;
        while (protocol == 0 || protocol == 43 || protocol == 60 || protocol == 51 || protocol == 44) {
            if (++extensions > 8) { view.state = ParseState::Unsupported; return view; }
            if (end - offset < 8) return view;
            if (protocol == 44) { view.state = ParseState::Fragment; return view; }
            const auto extension_length = (static_cast<std::size_t>(data[offset + 1]) + (protocol == 51 ? 2 : 1)) * (protocol == 51 ? 4 : 8);
            if (extension_length < 8 || extension_length > end - offset) return view;
            protocol = data[offset]; offset += extension_length;
        }
    } else { view.state = ParseState::Unsupported; return view; }
    if (protocol == 6) {
        if (end - offset < 20) return view;
        const auto header = static_cast<std::size_t>(data[offset + 12] >> 4) * 4;
        if (header < 20 || header > end - offset) return view;
        view.transport = Transport::Tcp; view.tcp_flags = data[offset + 13];
        view.payload = data.subspan(offset + header, end - offset - header);
        view.tls = classify_tls(view.payload);
    } else if (protocol == 17) {
        if (end - offset < 8) return view;
        const auto length = u16(data, offset + 4);
        if (length < 8 || length > end - offset) return view;
        view.transport = Transport::Udp;
        view.payload = data.subspan(offset + 8, length - 8);
    } else { view.state = ParseState::Unsupported; return view; }
    view.source.port = u16(data, offset); view.destination.port = u16(data, offset + 2);
    view.state = ParseState::Parsed;
    return view;
}
}
