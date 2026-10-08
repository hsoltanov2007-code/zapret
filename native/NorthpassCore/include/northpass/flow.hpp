#pragma once
#include "packet.hpp"
#include <chrono>
#include <unordered_map>
#include <list>
#include <string_view>

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
    int synack_direction{-1};
    std::array<std::uint32_t, 2> next_sequence{}, last_ack{};
    std::array<bool, 2> sequence_seen{}, ack_seen{}, fin_seen{};
    std::uint64_t retransmissions{}, out_of_order{}, duplicate_acks{};
    std::array<std::uint32_t, 2> syn_sequence{};
    bool tls_framing{};
};
class FlowTracker {
public:
    explicit FlowTracker(std::size_t capacity = 4096);
    const FlowSummary* observe(const PacketView& packet, std::size_t bytes, Clock::time_point now);
    void expire(Clock::time_point now);
    void expire_if_due(Clock::time_point now) { if (now >= next_expiry_) expire(now); }
    std::size_t size() const noexcept { return flows_.size(); }
    std::uint64_t evictions() const noexcept { return evictions_; }
private:
    std::size_t capacity_;
    Clock::time_point next_expiry_{};
    std::list<FlowKey> lru_;
    struct Entry { FlowSummary summary; std::list<FlowKey>::iterator position; };
    std::unordered_map<FlowKey, Entry, FlowHash> flows_;
    std::uint64_t evictions_{};
};
enum class StrategyAction { ForwardOriginal };
enum class StrategyCapability : std::uint32_t { Observe = 1, ForwardOriginal = 2 };
struct StrategyDefinition {
    std::uint32_t interface_version{2};
    std::string_view id{"original"};
    std::uint32_t capabilities{3};
    std::uint32_t maximum_mutations{};
};
void validate_strategy(const StrategyDefinition& definition);
class Strategy {
public:
    virtual ~Strategy() = default;
    virtual StrategyDefinition definition() const { return {}; }
    virtual StrategyAction inspect(const PacketView&, const FlowSummary*) = 0;
};
class PassThroughStrategy final : public Strategy {
public:
    StrategyAction inspect(const PacketView&, const FlowSummary*) override { return StrategyAction::ForwardOriginal; }
};
struct Counters { std::uint64_t packets{}, tcp{}, udp{}, tls{}, malformed{}, fragments{}, recoverable_errors{}, rollbacks{}; };
class PacketProcessor {
public:
    explicit PacketProcessor(Strategy& strategy, std::size_t capacity = 4096) : strategy_(strategy), flows_(capacity) { validate_strategy(strategy.definition()); }
    // Observation exceptions/invalid actions roll back to this exact input span.
    std::span<const std::uint8_t> process(std::span<const std::uint8_t> packet, Clock::time_point now) noexcept;
    void expire(Clock::time_point now) { flows_.expire_if_due(now); }
    const Counters& counters() const noexcept { return counters_; }
    std::size_t flows() const noexcept { return flows_.size(); }
private:
    Strategy& strategy_;
    FlowTracker flows_;
    Counters counters_{};
};
}
