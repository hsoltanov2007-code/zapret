#include "northpass/metrics.hpp"
#include <algorithm>
#include <iomanip>
#include <locale>
#include <sstream>
namespace northpass {
std::string Metrics::line(double cpu_percent, std::uint64_t memory_bytes) const {
    const auto elapsed = std::max<std::uint64_t>(1, monotonic_ns() - started_ns_);
    const auto count = measured.load();
    std::ostringstream output; output.imbue(std::locale::classic()); output << std::fixed << std::setprecision(3);
    output << "NORTHPASS_METRICS protocol=2 cpu_percent=" << cpu_percent << " memory_bytes=" << memory_bytes
        << " packets_per_second=" << static_cast<double>(captured.load()) * 1e9 / static_cast<double>(elapsed)
        << " latency_us=" << (count ? static_cast<double>(latency_total_ns.load()) / static_cast<double>(count) / 1000 : 0)
        << " max_latency_us=" << static_cast<double>(latency_max_ns.load()) / 1000
        << " queue_size=" << queue_size.load() << " queue_capacity=" << queue_capacity << " queue_peak=" << queue_peak.load()
        << " captured=" << captured.load() << " forwarded=" << forwarded.load() << " dropped_known=" << dropped_known.load()
        << " kernel_loss_unknown=1 backpressure=" << backpressure.load() << " active_flows=" << active_flows.load()
        << " recoverable_errors=" << recoverable.load() << " fatal_errors=" << fatal.load() << " auth_failures=" << auth_failures.load();
    return output.str();
}
}
