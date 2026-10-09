#pragma once
#include <array>
#include <algorithm>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <span>
#include <stdexcept>
namespace northpass {
constexpr std::size_t MaximumPacketSize = 40 + 65535;
struct QueuedPacket {
    std::array<std::uint8_t, MaximumPacketSize> bytes{};
    std::array<std::uint8_t, 80> metadata{};
    std::size_t length{};
    std::uint64_t captured_ns{};
};
// Fixed allocation before capture. Mutex protects multi-producer/consumer close
// races; no packet allocation and no blocking producer on saturation.
class PacketQueue {
public:
    explicit PacketQueue(std::size_t capacity = 8) : capacity_(capacity) {
        if (capacity == 0 || capacity > 64) throw std::invalid_argument("Queue capacity must be 1..64.");
        slots_ = std::make_unique<QueuedPacket[]>(capacity);
    }
    bool try_push(const QueuedPacket& packet) {
        if (packet.length == 0 || packet.length > MaximumPacketSize) throw std::invalid_argument("Invalid queued packet size.");
        std::lock_guard lock(mutex_);
        if (closed_ || size_ == capacity_) return false;
        auto& slot = slots_[(head_ + size_) % capacity_]; copy(packet, slot); ++size_;
        if (size_ > peak_) peak_ = size_;
        changed_.notify_one(); return true;
    }
    bool pop(QueuedPacket& packet, std::chrono::milliseconds timeout) {
        std::unique_lock lock(mutex_);
        changed_.wait_for(lock, timeout, [&] { return size_ != 0 || closed_; });
        if (!size_) return false;
        copy(slots_[head_], packet); head_ = (head_ + 1) % capacity_; --size_; return true;
    }
    void close() { std::lock_guard lock(mutex_); closed_ = true; changed_.notify_all(); }
    bool drained() const { std::lock_guard lock(mutex_); return closed_ && size_ == 0; }
    std::size_t size() const { std::lock_guard lock(mutex_); return size_; }
    std::size_t peak() const { std::lock_guard lock(mutex_); return peak_; }
    std::size_t capacity() const noexcept { return capacity_; }
private:
    static void copy(const QueuedPacket& source, QueuedPacket& target) {
        target.length = source.length; target.metadata = source.metadata; target.captured_ns = source.captured_ns;
        std::copy_n(source.bytes.begin(), source.length, target.bytes.begin());
    }
    const std::size_t capacity_;
    std::unique_ptr<QueuedPacket[]> slots_;
    mutable std::mutex mutex_;
    std::condition_variable changed_;
    std::size_t head_{}, size_{}, peak_{};
    bool closed_{};
};
}
