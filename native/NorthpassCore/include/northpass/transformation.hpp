#pragma once
#include "packet.hpp"
#include "queue.hpp"
#include <vector>
#include <string>
namespace northpass {
enum class TransformCapability : std::uint32_t { None = 0, SyntheticByteReplacement = 1 };
enum class TransformScope { Synthetic, Live };
struct TransformConfiguration {
    std::uint32_t version{3};
    TransformScope scope{TransformScope::Synthetic};
    TransformCapability capabilities{TransformCapability::None};
    std::size_t mtu{1500};
    bool operator==(const TransformConfiguration&) const = default;
};
struct ByteReplacement { std::size_t offset{}; std::vector<std::uint8_t> bytes; };
struct TransformProposal { std::uint32_t version{3}; std::vector<ByteReplacement> replacements; };
// Owns immutable original bytes: a rejected transaction cannot corrupt fallback.
class PacketTransaction {
public:
    PacketTransaction(std::span<const std::uint8_t> original, TransformConfiguration configuration);
    bool propose(const TransformProposal& proposal) noexcept;
    bool commit() noexcept;
    void rollback() noexcept;
    std::span<const std::uint8_t> original() const noexcept { return original_; }
    std::span<const std::uint8_t> result() const noexcept { return committed_ ? std::span<const std::uint8_t>(candidate_) : original(); }
    bool committed() const noexcept { return committed_; }
private:
    const std::vector<std::uint8_t> original_;
    const TransformConfiguration configuration_;
    std::vector<std::uint8_t> candidate_;
    bool proposed_{}, committed_{};
};
class TransformStrategy {
public:
    virtual ~TransformStrategy() = default;
    virtual void initialize(const TransformConfiguration&) = 0;
    virtual TransformProposal propose(std::span<const std::uint8_t>) = 0;
    virtual void shutdown() noexcept = 0;
};
// Always shuts down an initialized strategy and returns original bytes on errors.
std::vector<std::uint8_t> evaluate_synthetic(TransformStrategy&, std::span<const std::uint8_t>, TransformConfiguration);

struct PacketMetadata {
    bool outbound{}, loopback{}, impostor{}, ipv6{}, ip_checksum{}, tcp_checksum{}, udp_checksum{};
};
enum class ChecksumObservation { Valid, Invalid, OffloadUnverified, Unsupported };
ChecksumObservation observe_checksum(std::span<const std::uint8_t>, PacketMetadata) noexcept;
bool metadata_consistent(std::span<const std::uint8_t>, PacketMetadata) noexcept;
}
