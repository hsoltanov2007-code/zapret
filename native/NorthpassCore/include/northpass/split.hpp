#pragma once
#include "transformation.hpp"
#include "flow.hpp"
#include <optional>
namespace northpass {
enum class ClientHelloState { Complete, Incomplete, Invalid, Unsupported };
// Lengths only, never a retained hostname, credential or browsing payload.
struct ClientHelloFraming { ClientHelloState state{ClientHelloState::Invalid}; std::size_t record_bytes{}; };
ClientHelloFraming parse_client_hello(std::span<const std::uint8_t>) noexcept;
struct SplitConfiguration {
    std::uint32_t version{4};
    std::size_t mtu{1500}, first_payload{9};
    TransformScope scope{TransformScope::Synthetic};
    bool operator==(const SplitConfiguration&) const = default;
};
enum class SplitRejection { None, UnknownState, Layout, Framing, Capacity };
struct SplitDecision {
    std::optional<SegmentProposal> proposal;
    SplitRejection rejection{SplitRejection::None};
    bool retransmission{};
};
class NorthpassSplit {
public:
    void initialize(SplitConfiguration);
    SplitDecision propose(std::span<const std::uint8_t>, const FlowSummary*) const;
    void shutdown() noexcept { initialized_ = false; }
    TransformConfiguration transaction_configuration() const;
private:
    SplitConfiguration configuration_{};
    bool initialized_{};
};
// Deliberately packet-local toy DPI fixture, not a model of any actual ISP.
bool simulator_recognizes_client_hello(std::span<const std::uint8_t> packet) noexcept;
}
