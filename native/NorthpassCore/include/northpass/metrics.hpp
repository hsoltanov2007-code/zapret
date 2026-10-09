#pragma once
#include <atomic>
#include <chrono>
#include <cstdint>
#include <string>
namespace northpass {
inline std::uint64_t monotonic_ns() noexcept {
    return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}
class Metrics {
public:
    std::atomic<std::uint64_t> captured{}, forwarded{}, dropped_known{}, backpressure{}, recoverable{}, fatal{},
        latency_total_ns{}, latency_max_ns{}, measured{}, active_flows{}, queue_size{}, queue_peak{}, auth_failures{},
        tcp{}, udp{}, tls{}, malformed{}, fragments{}, rollbacks{};
    static constexpr std::uint64_t queue_capacity = 8;
    void record_latency(std::uint64_t elapsed) noexcept {
        latency_total_ns.fetch_add(elapsed); measured.fetch_add(1);
        auto previous = latency_max_ns.load();
        while (previous < elapsed && !latency_max_ns.compare_exchange_weak(previous, elapsed)) { }
    }
    std::string line(double cpu_percent = 0, std::uint64_t memory_bytes = 0) const;
private: const std::uint64_t started_ns_ = monotonic_ns();
};
}
