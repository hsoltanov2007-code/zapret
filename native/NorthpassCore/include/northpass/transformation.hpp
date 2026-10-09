#pragma once
#include "packet.hpp"
#include "queue.hpp"
#include <vector>
#include <string>
namespace northpass {
enum class TransformCapability : std::uint32_t { None = 0, SyntheticByteReplacement = 1, TcpSegmentation = 2 };
enum class TransformScope { Synthetic, Live, Laboratory };
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

// v4 segmentation is a separate contract. Live scope is always forbidden.
struct SegmentProposal { std::uint32_t version{4}; std::vector<std::vector<std::uint8_t>> packets; };
enum class SegmentFailure { None, Configuration, Layout, Count, Bounds, Integrity, Transmission };
class SegmentTransaction {
public:
    SegmentTransaction(std::span<const std::uint8_t>, TransformConfiguration);
    bool propose(const SegmentProposal&) noexcept;
    bool commit() noexcept;
    // Rollback is possible only BEFORE transmission. Never resend an original
    // after a partial/ambiguous send: doing so is not transactional rollback.
    bool rollback() noexcept;
    bool record_sent(std::size_t index) noexcept;
    void send_failed() noexcept { failure_ = SegmentFailure::Transmission; transmission_failed_ = true; }
    std::span<const std::uint8_t> original() const noexcept { return original_; }
    const std::vector<std::vector<std::uint8_t>>& segments() const noexcept {
        static const std::vector<std::vector<std::uint8_t>> empty;
        return committed_ ? segments_ : empty;
    }
    bool committed() const noexcept { return committed_; }
    bool fallback_allowed() const noexcept { return sent_ == 0 && !transmission_failed_; }
    std::size_t sent() const noexcept { return sent_; }
    SegmentFailure failure() const noexcept { return failure_; }
private:
    const std::vector<std::uint8_t> original_;
    const TransformConfiguration configuration_;
    std::vector<std::vector<std::uint8_t>> segments_;
    std::size_t sent_{};
    bool proposed_{}, committed_{}, transmission_failed_{};
    SegmentFailure failure_{SegmentFailure::None};
};
bool segmentation_layout(std::span<const std::uint8_t>) noexcept;
bool calculate_tcp_checksums(std::span<std::uint8_t>) noexcept;
std::vector<std::uint8_t> make_tcp_segment(std::span<const std::uint8_t> original,
    std::size_t offset, std::size_t length, std::size_t index, bool last);

struct PacketMetadata {
    bool outbound{}, loopback{}, impostor{}, ipv6{}, ip_checksum{}, tcp_checksum{}, udp_checksum{};
};
enum class ChecksumObservation { Valid, Invalid, OffloadUnverified, Unsupported };
ChecksumObservation observe_checksum(std::span<const std::uint8_t>, PacketMetadata) noexcept;
// Windows loopback may omit checksum bytes even with validity metadata. Only
// exact loopback lab inputs normalize absent fields in a temporary validation
// copy, never in the immutable original or consumer pass-through packet.
ChecksumObservation observe_split_input_checksum(std::span<const std::uint8_t>, PacketMetadata) noexcept;
bool metadata_consistent(std::span<const std::uint8_t>, PacketMetadata) noexcept;
}
