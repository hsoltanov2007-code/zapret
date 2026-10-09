#include "northpass/transformation.hpp"
#include <algorithm>
#include <stdexcept>
namespace northpass {
namespace {
void put16(std::span<std::uint8_t> bytes, std::size_t at, std::size_t value) noexcept {
    bytes[at] = static_cast<std::uint8_t>(value >> 8); bytes[at + 1] = static_cast<std::uint8_t>(value);
}
void put32(std::span<std::uint8_t> bytes, std::size_t at, std::uint32_t value) noexcept {
    for (unsigned i = 0; i < 4; ++i) bytes[at + i] = static_cast<std::uint8_t>(value >> (24 - 8 * i));
}
std::uint32_t sum(std::span<const std::uint8_t> bytes, std::uint32_t current = 0) noexcept {
    for (std::size_t i = 0; i < bytes.size(); i += 2)
        current += (static_cast<std::uint32_t>(bytes[i]) << 8) | (i + 1 < bytes.size() ? bytes[i + 1] : 0);
    while (current >> 16) current = (current & 65535) + (current >> 16);
    return current;
}
std::vector<std::uint8_t> snapshot(std::span<const std::uint8_t> packet) {
    if (packet.empty() || packet.size() > MaximumPacketSize) throw std::invalid_argument("Bounded original snapshot required.");
    return {packet.begin(), packet.end()};
}
}
bool segmentation_layout(std::span<const std::uint8_t> bytes) noexcept {
    const auto p = classify(bytes);
    if (p.state != ParseState::Parsed || p.transport != Transport::Tcp || p.payload.empty() ||
        p.extension_count || p.atomic_fragment || (p.ip_version == 4 && p.transport_offset != 20) ||
        (p.ip_version == 6 && p.transport_offset != 40) || (p.tcp_flags & ~0x18u) || !(p.tcp_flags & 0x10) ||
        bytes[p.transport_offset + 12] & 1 || bytes[p.transport_offset + 18] || bytes[p.transport_offset + 19]) return false;
    // Only EOL, NOP and timestamps have reviewed per-segment semantics here.
    const auto end = bytes.size() - p.payload.size();
    bool timestamp_seen = false;
    for (std::size_t at = p.transport_offset + 20; at < end;) {
        if (bytes[at] == 0) { for (; at < end; ++at) if (bytes[at] != 0) return false; break; }
        if (bytes[at] == 1) { ++at; continue; }
        if (bytes[at] != 8 || end - at < 10 || bytes[at + 1] != 10 || timestamp_seen) return false;
        timestamp_seen = true;
        at += 10;
    }
    return true;
}
bool calculate_tcp_checksums(std::span<std::uint8_t> bytes) noexcept {
    if (!segmentation_layout(bytes)) return false;
    const auto p = classify(bytes); const auto tcp = p.transport_offset;
    if (p.ip_version == 4) {
        put16(bytes, 10, 0); put16(bytes, 10, (~sum(bytes.first(20))) & 65535);
    }
    put16(bytes, tcp + 16, 0);
    std::array<std::uint8_t, 40> pseudo{}; std::size_t length{};
    const auto tcp_length = bytes.size() - tcp;
    if (p.ip_version == 4) {
        std::copy_n(bytes.begin() + 12, 8, pseudo.begin()); pseudo[9] = 6;
        put16(pseudo, 10, tcp_length); length = 12;
    } else {
        std::copy_n(bytes.begin() + 8, 32, pseudo.begin()); put32(pseudo, 32, static_cast<std::uint32_t>(tcp_length));
        pseudo[39] = 6; length = 40;
    }
    put16(bytes, tcp + 16, (~sum(bytes.subspan(tcp), sum(std::span(pseudo).first(length)))) & 65535);
    return true;
}
ChecksumObservation observe_split_input_checksum(std::span<const std::uint8_t> bytes, PacketMetadata m) noexcept {
    const auto p = classify(bytes);
    Endpoint loopback; loopback.address[15] = 1; if (p.ip_version == 4) loopback.address[12] = 127;
    if (!m.outbound || !m.loopback || m.impostor || !segmentation_layout(bytes) ||
        p.source.address != loopback.address || p.destination.address != loopback.address)
        return observe_checksum(bytes, m);
    const bool absent_ip = p.ip_version == 4 && bytes[10] == 0 && bytes[11] == 0;
    const bool absent_tcp = bytes[p.transport_offset + 16] == 0 && bytes[p.transport_offset + 17] == 0;
    if (!absent_ip && !absent_tcp) return observe_checksum(bytes, m);
    try {
        auto validation = snapshot(bytes);
        if (!calculate_tcp_checksums(validation)) return ChecksumObservation::Unsupported;
        if (p.ip_version == 4 && !absent_ip) { validation[10] = bytes[10]; validation[11] = bytes[11]; }
        // Any nonzero original TCP checksum is still independently checked.
        if (!absent_tcp) { validation[p.transport_offset + 16] = bytes[p.transport_offset + 16]; validation[p.transport_offset + 17] = bytes[p.transport_offset + 17]; }
        if (absent_ip) m.ip_checksum = true;
        if (absent_tcp) m.tcp_checksum = true;
        const auto result = observe_checksum(validation, m);
        return result == ChecksumObservation::Valid ? ChecksumObservation::OffloadUnverified : result;
    } catch (...) { return ChecksumObservation::Unsupported; }
}
std::vector<std::uint8_t> make_tcp_segment(std::span<const std::uint8_t> original,
    std::size_t offset, std::size_t length, std::size_t index, bool last) {
    if (!segmentation_layout(original) || index >= 16) throw std::invalid_argument("Unsupported segmentation layout.");
    const auto p = classify(original); const auto header = original.size() - p.payload.size();
    if (!length || offset > p.payload.size() || length > p.payload.size() - offset || last != (offset + length == p.payload.size()))
        throw std::invalid_argument("Invalid segment range.");
    std::vector<std::uint8_t> result(original.begin(), original.begin() + static_cast<std::ptrdiff_t>(header));
    result.insert(result.end(), p.payload.begin() + static_cast<std::ptrdiff_t>(offset), p.payload.begin() + static_cast<std::ptrdiff_t>(offset + length));
    if (p.ip_version == 4) {
        put16(result, 2, result.size());
        const auto id = (static_cast<unsigned>(original[4]) << 8) | original[5];
        put16(result, 4, static_cast<std::uint16_t>(id + index));
    } else put16(result, 4, result.size() - 40);
    put32(result, p.transport_offset + 4, p.sequence + static_cast<std::uint32_t>(offset)); // modulo 2^32
    if (!last) result[p.transport_offset + 13] &= static_cast<std::uint8_t>(~8u); // PSH only on final segment
    if (!calculate_tcp_checksums(result)) throw std::invalid_argument("Segment checksum layout rejected.");
    return result;
}
SegmentTransaction::SegmentTransaction(std::span<const std::uint8_t> bytes, TransformConfiguration c)
    : original_(snapshot(bytes)), configuration_(c) {
    if (c.version != 4 || c.capabilities != TransformCapability::TcpSegmentation || c.mtu < 1280 || c.mtu > 1500 ||
        (c.scope != TransformScope::Synthetic && c.scope != TransformScope::Laboratory))
        throw std::invalid_argument("Unsupported segmentation ABI/capability/scope/MTU.");
    if (!segmentation_layout(original_)) throw std::invalid_argument("Unsupported original packet layout.");
}
bool SegmentTransaction::rollback() noexcept {
    if (!fallback_allowed()) return false;
    segments_.clear(); proposed_ = committed_ = false; failure_ = SegmentFailure::None; return true;
}
bool SegmentTransaction::propose(const SegmentProposal& proposal) noexcept {
    if (!rollback()) return false;
    const auto reject = [&](SegmentFailure failure) { segments_.clear(); proposed_ = committed_ = false; failure_ = failure; return false; };
    try {
        if (proposal.version != 4) return reject(SegmentFailure::Configuration);
        if (proposal.packets.size() < 2 || proposal.packets.size() > 16) return reject(SegmentFailure::Count);
        const auto p = classify(original_); const auto header = original_.size() - p.payload.size();
        std::size_t offset{};
        for (std::size_t i = 0; i < proposal.packets.size(); ++i) {
            const auto& candidate = proposal.packets[i];
            if (candidate.size() <= header || candidate.size() > configuration_.mtu || !segmentation_layout(candidate)) return reject(SegmentFailure::Bounds);
            const auto length = candidate.size() - header;
            if (offset > p.payload.size() || length > p.payload.size() - offset) return reject(SegmentFailure::Bounds);
            const bool last = i + 1 == proposal.packets.size();
            if (last != (offset + length == p.payload.size())) return reject(SegmentFailure::Bounds);
            // Canonical equality checks every header byte, ordered sequence,
            // checksum, payload, no gaps/overlap and exact reconstruction.
            if (candidate != make_tcp_segment(original_, offset, length, i, last)) return reject(SegmentFailure::Integrity);
            offset += length;
        }
        segments_ = proposal.packets; proposed_ = true; return true;
    } catch (...) { return reject(SegmentFailure::Integrity); }
}
bool SegmentTransaction::commit() noexcept {
    if (!proposed_ || transmission_failed_ || sent_) return false;
    committed_ = true; return true;
}
bool SegmentTransaction::record_sent(std::size_t index) noexcept {
    if (!committed_ || transmission_failed_ || index != sent_ || index >= segments_.size()) return false;
    ++sent_; return true;
}
}
