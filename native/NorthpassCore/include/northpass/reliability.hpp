#pragma once
#include <cstdint>
#include <stdexcept>
#include <string_view>
namespace northpass {
enum class TestFault { None, DriverOpen, Send, ObserveDelay, Crash };
// Faults require both a dedicated loopback scope and an explicit test capability.
// Idle only permits driver startup failure; never a general-network test path.
inline TestFault parse_test_fault(std::string_view name, bool loopback, bool authorized) {
    if (!authorized) throw std::invalid_argument("Fault injection requires an explicit internal test capability.");
    if (name == "driver-open") return TestFault::DriverOpen;
    if (!loopback) throw std::invalid_argument("Packet faults require a dedicated loopback port.");
    if (name == "send") return TestFault::Send;
    if (name == "observation-delay") return TestFault::ObserveDelay;
    if (name == "crash") return TestFault::Crash;
    throw std::invalid_argument("Unknown reliability test fault.");
}
}
