#include "northpass/split.hpp"
#include <algorithm>
#include <stdexcept>
namespace northpass {
namespace {
std::size_t word(std::span<const std::uint8_t> p, std::size_t at) noexcept {
    return (static_cast<std::size_t>(p[at]) << 8) | p[at + 1];
}
}
ClientHelloFraming parse_client_hello(std::span<const std::uint8_t> p) noexcept {
    if (p.size() < 5) return {ClientHelloState::Incomplete, 0};
    if (p[0] != 22 || p[1] != 3 || p[2] < 1 || p[2] > 3) return {};
    const auto record = word(p, 3);
    if (record < 4 || record > 16384) return {};
    if (record + 5 > p.size()) return {ClientHelloState::Incomplete, record + 5};
    if (record + 5 != p.size()) return {ClientHelloState::Unsupported, record + 5};
    if (p[5] != 1) return {};
    const auto body = (static_cast<std::size_t>(p[6]) << 16) | word(p, 7);
    if (body + 4 != record || body < 41 || p[9] != 3 || p[10] < 1 || p[10] > 3) return {};
    std::size_t at = 43; // legacy_version + random, preceded by record/handshake headers
    const auto session = p[at++];
    if (session > 32 || session > p.size() - at) return {};
    at += session;
    if (p.size() - at < 2) return {};
    const auto ciphers = word(p, at); at += 2;
    if (ciphers == 0 || ciphers % 2 || ciphers > p.size() - at) return {};
    at += ciphers;
    if (p.size() - at < 2 || p[at] != 1 || p[at + 1] != 0) return {};
    at += 2;
    if (at == p.size()) return {ClientHelloState::Complete, p.size()}; // TLS 1.2 without extensions
    if (p.size() - at < 2) return {};
    const auto extensions = word(p, at); at += 2;
    if (extensions != p.size() - at) return {};
    std::array<std::uint16_t, 64> types{}; std::size_t count{};
    while (at < p.size()) {
        if (p.size() - at < 4 || count == types.size()) return {};
        const auto type = static_cast<std::uint16_t>(word(p, at));
        if (std::find(types.begin(), types.begin() + static_cast<std::ptrdiff_t>(count), type) != types.begin() + static_cast<std::ptrdiff_t>(count)) return {};
        types[count++] = type;
        const auto length = word(p, at + 2); at += 4;
        if (length > p.size() - at) return {};
        at += length; // opaque extension bodies are neither decoded nor retained
    }
    return {ClientHelloState::Complete, p.size()};
}
void NorthpassSplit::initialize(SplitConfiguration c) {
    initialized_ = false;
    if (c.version != 4 || c.mtu < 1280 || c.mtu > 1500 || c.first_payload == 0 || c.first_payload > 64 ||
        (c.scope != TransformScope::Synthetic && c.scope != TransformScope::Laboratory))
        throw std::invalid_argument("Northpass Split requires ABI 4, MTU 1280..1500, first payload 1..64 and synthetic/isolated-lab scope.");
    configuration_ = c; initialized_ = true;
}
TransformConfiguration NorthpassSplit::transaction_configuration() const {
    if (!initialized_) throw std::logic_error("Northpass Split is not initialized.");
    return {4, configuration_.scope, TransformCapability::TcpSegmentation, configuration_.mtu};
}
SplitDecision NorthpassSplit::propose(std::span<const std::uint8_t> bytes, const FlowSummary* flow) const {
    if (!initialized_) throw std::logic_error("Northpass Split is not initialized.");
    const auto view = classify(bytes);
    if (!segmentation_layout(bytes)) return {{}, SplitRejection::Layout, false};
    const auto direction = view.source <= view.destination ? 0u : 1u;
    if (!flow || flow->tcp != TcpObservation::Established || flow->syn_direction != static_cast<int>(direction) ||
        flow->synack_direction != static_cast<int>(1u - direction) || !flow->sequence_seen[direction] ||
        view.sequence != flow->syn_sequence[direction] + 1 || view.acknowledgement != flow->syn_sequence[1u - direction] + 1)
        return {{}, SplitRejection::UnknownState, false};
    if (parse_client_hello(view.payload).state != ClientHelloState::Complete || view.payload.size() <= configuration_.first_payload)
        return {{}, SplitRejection::Framing, false};
    const auto header = bytes.size() - view.payload.size();
    if (configuration_.mtu <= header || configuration_.first_payload > configuration_.mtu - header)
        return {{}, SplitRejection::Capacity, false};
    SegmentProposal proposal;
    for (std::size_t at = 0; at < view.payload.size();) {
        if (proposal.packets.size() == 16) return {{}, SplitRejection::Capacity, false};
        const auto length = std::min(at == 0 ? configuration_.first_payload : configuration_.mtu - header, view.payload.size() - at);
        proposal.packets.push_back(make_tcp_segment(bytes, at, length, proposal.packets.size(), at + length == view.payload.size()));
        at += length;
    }
    return {std::move(proposal), SplitRejection::None, flow->retransmissions != 0};
}
bool simulator_recognizes_client_hello(std::span<const std::uint8_t> packet) noexcept {
    const auto view = classify(packet);
    return view.state == ParseState::Parsed && view.transport == Transport::Tcp &&
        parse_client_hello(view.payload).state == ClientHelloState::Complete;
}
}
