#include "northpass/flow.hpp"
#include <algorithm>
#include <stdexcept>

namespace northpass {
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
    std::erase_if(flows_, [now](const auto& entry) {
        const auto idle = entry.first.transport == Transport::Udp ? std::chrono::seconds(30) : std::chrono::seconds(120);
        return now - entry.second.last_seen >= idle;
    });
    next_expiry_ = now + std::chrono::seconds(1);
}
const FlowSummary* FlowTracker::observe(const PacketView& packet, std::size_t bytes, Clock::time_point now) {
    if (packet.state != ParseState::Parsed) return nullptr;
    if (now >= next_expiry_) expire(now);
    const bool forward = packet.source <= packet.destination;
    FlowKey key{packet.ip_version, packet.transport, forward ? packet.source : packet.destination, forward ? packet.destination : packet.source};
    auto found = flows_.find(key);
    if (found == flows_.end()) {
        if (flows_.size() == capacity_) {
            const auto oldest = std::min_element(flows_.begin(), flows_.end(), [](const auto& a, const auto& b) { return a.second.last_seen < b.second.last_seen; });
            flows_.erase(oldest);
        }
        found = flows_.emplace(key, FlowSummary{}).first;
    }
    auto& flow = found->second;
    const auto direction = forward ? 0u : 1u;
    ++flow.packets[direction]; flow.bytes[direction] += bytes;
    flow.last_seen = std::max(flow.last_seen, now);
    flow.tls_framing |= packet.tls != TlsKind::Unknown;
    if (packet.transport == Transport::Tcp) {
        const auto flags = packet.tcp_flags;
        if ((flags & 4) != 0) flow.tcp = TcpObservation::Reset;
        else if ((flags & 1) != 0) flow.tcp = TcpObservation::Closing;
        else if ((flags & 0x12) == 0x12) flow.tcp = TcpObservation::SynAck;
        else if ((flags & 2) != 0) flow.tcp = TcpObservation::Syn;
        else if ((flags & 0x10) != 0 && flow.tcp == TcpObservation::SynAck) flow.tcp = TcpObservation::Established;
    }
    return &flow;
}
std::span<const std::uint8_t> PacketProcessor::process(std::span<const std::uint8_t> packet, Clock::time_point now) {
    ++counters_.packets;
    const auto view = classify(packet);
    if (view.state == ParseState::Malformed) ++counters_.malformed;
    if (view.state == ParseState::Fragment) ++counters_.fragments;
    if (view.transport == Transport::Tcp) ++counters_.tcp;
    if (view.transport == Transport::Udp) ++counters_.udp;
    if (view.tls != TlsKind::Unknown) ++counters_.tls;
    const auto* flow = flows_.observe(view, packet.size(), now);
    if (strategy_.inspect(view, flow) != StrategyAction::ForwardOriginal) throw std::runtime_error("v0.1 only permits forwarding original bytes.");
    return packet;
}
}
