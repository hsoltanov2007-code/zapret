#pragma once
#include "packet.hpp"
#include <chrono>
#include <unordered_map>

namespace northpass {
using Clock = std::chrono::steady_clock;
struct FlowKey {
    std::uint8_t ip_version{};
    Transport transport{};
    Endpoint a{}, b{};
    bool operator==(const FlowKey&) const = default;
};
struct FlowHash { std::size_t operator()(const FlowKey& key) const noexcept; };
enum class TcpObservation { Traffic, Syn, SynAck, Established, Closing, Reset };
struct FlowSummary {
    std::array<std::uint64_t, 2> packets{}, bytes{};
    Clock::time_point last_seen{};
    TcpObservation tcp{TcpObservation::Traffic};
    int syn_direction{-1};
    bool tls_framing{};
};
class FlowTracker {
public:
    explicit FlowTracker(std::size_t capacity = 4096);
    const FlowSummary* observe(const PacketView& packet, std::size_t bytes, Clock::time_point now);
    void expire(Clock::time_point now);
    std::size_t size() const noexcept { return flows_.size(); }
private:
    std::size_t capacity_;
    Clock::time_point next_expiry_{};
    std::unordered_map<FlowKey, FlowSummary, FlowHash> flows_;
};
enum class StrategyAction { ForwardOriginal };
class Strategy {
public:
    virtual ~Strategy() = default;
    virtual StrategyAction inspect(const PacketView&, const FlowSummary*) = 0;
};
class PassThroughStrategy final : public Strategy {
public:
    StrategyAction inspect(const PacketView&, const FlowSummary*) override { return StrategyAction::ForwardOriginal; }
};
struct Counters { std::uint64_t packets{}, tcp{}, udp{}, tls{}, malformed{}, fragments{}; };
class PacketProcessor {
public:
    explicit PacketProcessor(Strategy& strategy, std::size_t capacity = 4096) : strategy_(strategy), flows_(capacity) {}
    // The forwarding buffer is the exact input span. v0.1 strategies cannot mutate it.
    std::span<const std::uint8_t> process(std::span<const std::uint8_t> packet, Clock::time_point now);
    const Counters& counters() const noexcept { return counters_; }
    std::size_t flows() const noexcept { return flows_.size(); }
private:
    Strategy& strategy_;
    FlowTracker flows_;
    Counters counters_{};
};
}
