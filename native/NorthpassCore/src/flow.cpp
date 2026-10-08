#include "northpass/flow.hpp"
#include <algorithm>
#include <bit>
#include <stdexcept>
namespace northpass {
namespace {
bool before(std::uint32_t a, std::uint32_t b) noexcept { return std::bit_cast<std::int32_t>(a - b) < 0; }
}
std::size_t FlowHash::operator()(const FlowKey& key) const noexcept {
    std::size_t value = 1469598103934665603ull;
    const auto add = [&value](std::uint8_t byte) { value = (value ^ byte) * 1099511628211ull; };
    add(key.ip_version); add(static_cast<std::uint8_t>(key.transport));
    for (const auto* endpoint : { &key.a, &key.b }) {
        for (auto byte : endpoint->address) add(byte);
        add(static_cast<std::uint8_t>(endpoint->port >> 8)); add(static_cast<std::uint8_t>(endpoint->port));
    }
    return value;
}
FlowTracker::FlowTracker(std::size_t capacity) : capacity_(capacity) {
    if (capacity == 0 || capacity > 4096) throw std::invalid_argument("Flow capacity must be between 1 and 4096.");
    flows_.reserve(capacity);
}
void FlowTracker::expire(Clock::time_point now) {
    for (auto it = flows_.begin(); it != flows_.end();) {
        const auto& flow = it->second.summary;
        const auto idle = it->first.transport == Transport::Udp ? std::chrono::seconds(30) :
            flow.tcp == TcpObservation::Closing || flow.tcp == TcpObservation::Reset ? std::chrono::seconds(5) : std::chrono::seconds(120);
        if (now - flow.last_seen >= idle) { lru_.erase(it->second.position); it = flows_.erase(it); }
        else ++it;
    }
    next_expiry_ = now + std::chrono::seconds(1);
}
const FlowSummary* FlowTracker::observe(const PacketView& packet, std::size_t bytes, Clock::time_point now) {
    if (packet.state != ParseState::Parsed) return nullptr;
    if (now >= next_expiry_) expire(now);
    const bool forward = packet.source <= packet.destination;
    FlowKey key{packet.ip_version, packet.transport, forward ? packet.source : packet.destination, forward ? packet.destination : packet.source};
    auto found = flows_.find(key);
    if (found == flows_.end()) {
        if (flows_.size() == capacity_) { flows_.erase(lru_.front()); lru_.pop_front(); ++evictions_; }
        lru_.push_back(key);
        try { found = flows_.emplace(key, Entry{FlowSummary{}, std::prev(lru_.end())}).first; }
        catch (...) { lru_.pop_back(); throw; }
    } else lru_.splice(lru_.end(), lru_, found->second.position);
    auto& flow = found->second.summary;
    const auto direction = forward ? 0u : 1u, opposite = 1u - direction;
    const auto flags = packet.tcp_flags;
    const bool syn = (flags & 2) != 0, ack = (flags & 0x10) != 0;
    // A new non-retransmitted SYN on a reused tuple starts a fresh observation.
    if (packet.transport == Transport::Tcp && syn && !ack && flow.sequence_seen[direction] &&
        (flow.syn_direction != static_cast<int>(direction) || packet.sequence != flow.syn_sequence[direction] ||
         flow.tcp == TcpObservation::Reset || flow.tcp == TcpObservation::Closing)) flow = {};
    ++flow.packets[direction]; flow.bytes[direction] += bytes;
    flow.last_seen = std::max(flow.last_seen, now);
    flow.tls_framing |= packet.tls != TlsKind::Unknown;
    if (packet.transport != Transport::Tcp) return &flow;
    const auto consumed = static_cast<std::uint32_t>(packet.payload.size()) + (syn ? 1u : 0u) + ((flags & 1) ? 1u : 0u);
    const auto end = packet.sequence + consumed;
    if (flow.sequence_seen[direction]) {
        if (consumed && before(packet.sequence, flow.next_sequence[direction])) ++flow.retransmissions; // overlap observation, not proof
        if (before(flow.next_sequence[direction], packet.sequence)) ++flow.out_of_order;
        if (before(flow.next_sequence[direction], end)) flow.next_sequence[direction] = end;
    } else { flow.sequence_seen[direction] = true; flow.next_sequence[direction] = end; }
    if (ack) {
        if (flow.ack_seen[direction] && packet.acknowledgement == flow.last_ack[direction] && consumed == 0) ++flow.duplicate_acks;
        if (!flow.ack_seen[direction] || before(flow.last_ack[direction], packet.acknowledgement)) flow.last_ack[direction] = packet.acknowledgement;
        flow.ack_seen[direction] = true;
    }
    if (flags & 4) flow.tcp = TcpObservation::Reset;
    else if (flags & 1) { flow.fin_seen[direction] = true; flow.tcp = TcpObservation::Closing; }
    else if (flow.tcp != TcpObservation::Reset && flow.tcp != TcpObservation::Closing) {
        if (syn && !ack && (flow.tcp == TcpObservation::Traffic || flow.tcp == TcpObservation::Syn)) {
            flow.tcp = TcpObservation::Syn; flow.syn_direction = static_cast<int>(direction); flow.syn_sequence[direction] = packet.sequence;
        }
        else if (syn && ack && (flow.tcp == TcpObservation::Syn || flow.tcp == TcpObservation::SynAck) &&
                 flow.syn_direction == static_cast<int>(opposite) && packet.acknowledgement == flow.syn_sequence[opposite] + 1) {
            flow.tcp = TcpObservation::SynAck; flow.synack_direction = static_cast<int>(direction); flow.syn_sequence[direction] = packet.sequence;
        } else if (ack && !syn && flow.tcp == TcpObservation::SynAck && flow.syn_direction == static_cast<int>(direction) &&
                   flow.synack_direction == static_cast<int>(opposite) && packet.acknowledgement == flow.syn_sequence[opposite] + 1) flow.tcp = TcpObservation::Established;
    }
    return &flow;
}
void validate_strategy(const StrategyDefinition& d) {
    if (d.interface_version != 2 || d.id != "original" || d.capabilities != 3 || d.maximum_mutations != 0)
        throw std::invalid_argument("Strategy ABI/capabilities/configuration are unsupported; v0.2 permits original forwarding only.");
}
std::span<const std::uint8_t> PacketProcessor::process(std::span<const std::uint8_t> packet, Clock::time_point now) noexcept {
    ++counters_.packets;
    const auto view = classify(packet);
    if (view.state == ParseState::Malformed) ++counters_.malformed;
    if (view.state == ParseState::Fragment) ++counters_.fragments;
    if (view.transport == Transport::Tcp) ++counters_.tcp;
    if (view.transport == Transport::Udp) ++counters_.udp;
    if (view.tls != TlsKind::Unknown) ++counters_.tls;
    try {
        const auto* flow = flows_.observe(view, packet.size(), now);
        if (strategy_.inspect(view, flow) != StrategyAction::ForwardOriginal) throw std::runtime_error("Unreviewed transformation action.");
    } catch (...) { ++counters_.recoverable_errors; ++counters_.rollbacks; }
    return packet;
}
}
