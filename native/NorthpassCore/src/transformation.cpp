#include "northpass/transformation.hpp"
#include <algorithm>
#include <stdexcept>
namespace northpass {
namespace {
void validate(const TransformConfiguration& c) {
    if (c.version != 3 || c.mtu < 68 || c.mtu > MaximumPacketSize ||
        (c.capabilities != TransformCapability::None && c.capabilities != TransformCapability::SyntheticByteReplacement) ||
        (c.scope == TransformScope::Live && c.capabilities != TransformCapability::None) ||
        (c.scope != TransformScope::Live && c.scope != TransformScope::Synthetic))
        throw std::invalid_argument("Unsupported transformation configuration/capability/scope.");
}
std::vector<std::uint8_t> bounded_copy(std::span<const std::uint8_t> packet) {
    if (packet.empty() || packet.size() > MaximumPacketSize) throw std::invalid_argument("Packet snapshot exceeds its bounded capacity.");
    return {packet.begin(), packet.end()};
}
bool checksum(std::span<const std::uint8_t> bytes) noexcept {
    std::uint32_t sum{};
    for (std::size_t i = 0; i < bytes.size(); i += 2)
        sum += (static_cast<std::uint32_t>(bytes[i]) << 8) | (i + 1 < bytes.size() ? bytes[i + 1] : 0);
    while (sum >> 16) sum = (sum & 0xffff) + (sum >> 16);
    return sum == 0xffff;
}
}
PacketTransaction::PacketTransaction(std::span<const std::uint8_t> packet, TransformConfiguration c)
    : original_(bounded_copy(packet)), configuration_(c) {
    validate(c);
    if (packet.empty() || packet.size() > MaximumPacketSize || packet.size() > c.mtu || classify(packet).state != ParseState::Parsed)
        throw std::invalid_argument("Transformation requires a complete structurally valid packet within the configured MTU.");
}
bool PacketTransaction::propose(const TransformProposal& p) noexcept {
    rollback();
    try {
        if (p.version != 3 || p.replacements.size() > 16 ||
            (!p.replacements.empty() && (configuration_.scope != TransformScope::Synthetic || configuration_.capabilities != TransformCapability::SyntheticByteReplacement))) return false;
        auto candidate = original_; std::size_t end{};
        for (const auto& change : p.replacements) {
            if (change.bytes.empty() || change.offset < end || change.offset > candidate.size() || change.bytes.size() > candidate.size() - change.offset) return false;
            std::copy(change.bytes.begin(), change.bytes.end(), candidate.begin() + static_cast<std::ptrdiff_t>(change.offset));
            end = change.offset + change.bytes.size();
        }
        if (candidate.size() != original_.size() || classify(candidate).state != ParseState::Parsed) return false;
        candidate_ = std::move(candidate); proposed_ = true; return true;
    } catch (...) { rollback(); return false; }
}
bool PacketTransaction::commit() noexcept {
    if (!proposed_) { rollback(); return false; }
    committed_ = true; return true;
}
void PacketTransaction::rollback() noexcept { candidate_.clear(); proposed_ = false; committed_ = false; }
std::vector<std::uint8_t> evaluate_synthetic(TransformStrategy& strategy, std::span<const std::uint8_t> bytes, TransformConfiguration c) {
    auto original = bounded_copy(bytes);
    if (c.scope != TransformScope::Synthetic) return original;
    struct Cleanup { TransformStrategy& strategy; ~Cleanup() { strategy.shutdown(); } } cleanup{strategy};
    try {
        PacketTransaction transaction(bytes, c); strategy.initialize(c);
        if (transaction.propose(strategy.propose(transaction.original())) && transaction.commit()) return {transaction.result().begin(), transaction.result().end()};
    } catch (...) { }
    return original;
}
bool metadata_consistent(std::span<const std::uint8_t> packet, PacketMetadata m) noexcept {
    return !packet.empty() && ((packet[0] >> 4) == 6) == m.ipv6 && !m.impostor;
}
ChecksumObservation observe_checksum(std::span<const std::uint8_t> packet, PacketMetadata m) noexcept {
    const auto view = classify(packet);
    if (!metadata_consistent(packet, m) || view.state != ParseState::Parsed) return ChecksumObservation::Unsupported;
    // Offload validity bits are observational; original bytes and metadata are
    // always reinjected first. Never repair or recompute live packet checksums.
    if (view.ip_version == 4 && !m.ip_checksum) return ChecksumObservation::OffloadUnverified;
    if (view.ip_version == 4 && !checksum(packet.first(static_cast<std::size_t>(packet[0] & 15) * 4))) return ChecksumObservation::Invalid;
    if ((view.transport == Transport::Tcp && !m.tcp_checksum) || (view.transport == Transport::Udp && !m.udp_checksum)) return ChecksumObservation::OffloadUnverified;
    if (view.extension_count != 0) return ChecksumObservation::Unsupported; // routing/AH pseudo-header semantics deferred
    const auto segment = packet.subspan(view.transport_offset);
    const auto checksum_offset = view.transport == Transport::Udp ? 6u : 16u;
    if (view.transport == Transport::Udp && segment[checksum_offset] == 0 && segment[checksum_offset+1] == 0)
        return view.ip_version == 4 ? ChecksumObservation::Unsupported : ChecksumObservation::Invalid; // IPv4 checksum optional
    std::vector<std::uint8_t> pseudo;
    try {
        if (view.ip_version == 4) {
            pseudo.insert(pseudo.end(),packet.begin()+12,packet.begin()+20);
            pseudo.push_back(0);pseudo.push_back(static_cast<std::uint8_t>(view.transport));
            pseudo.push_back(static_cast<std::uint8_t>(segment.size()>>8));pseudo.push_back(static_cast<std::uint8_t>(segment.size()));
        } else {
            pseudo.insert(pseudo.end(),packet.begin()+8,packet.begin()+40);
            for(int shift:{24,16,8,0})pseudo.push_back(static_cast<std::uint8_t>(segment.size()>>shift));
            pseudo.insert(pseudo.end(),{0,0,0,static_cast<std::uint8_t>(view.transport)});
        }
        pseudo.insert(pseudo.end(),segment.begin(),segment.end());
        return checksum(pseudo) ? ChecksumObservation::Valid : ChecksumObservation::Invalid;
    } catch (...) { return ChecksumObservation::Unsupported; }
}
}
