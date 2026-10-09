#include "northpass/flow.hpp"
#include "northpass/options.hpp"
#include "northpass/windows_security.hpp"
#include "northpass/windows_ipc.hpp"
#include "northpass/queue.hpp"
#include "northpass/transformation.hpp"
#include "northpass/split.hpp"
#include <psapi.h>
#include <algorithm>
#include <cstring>
#include <memory>
#include <mutex>
#include <tlhelp32.h>
#define WINDIVERTEXPORT
#include <windivert.h>
#include <array>
#include <atomic>
#include <bit>
#include <iostream>
#include <thread>
#include <stdexcept>
#include <vector>
#include <sstream>
namespace {
using namespace northpass;
std::atomic<HANDLE> control_stop{};
BOOL WINAPI console_control(DWORD type) {
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT || type == CTRL_CLOSE_EVENT || type == CTRL_SHUTDOWN_EVENT) {
        if (const auto stop = control_stop.load()) SetEvent(stop);
        return TRUE;
    }
    return FALSE;
}
template<class Function> Function function(HMODULE module, const char* name) {
    const auto address = GetProcAddress(module, name);
    if (!address) throw std::runtime_error("Verified WinDivert SDK export missing.");
    return std::bit_cast<Function>(address);
}
struct Divert {
    HMODULE module{};
    decltype(&WinDivertOpen) open{};
    decltype(&WinDivertRecvEx) receive{};
    decltype(&WinDivertSend) send{};
    decltype(&WinDivertShutdown) shutdown{};
    decltype(&WinDivertClose) close{};
    decltype(&WinDivertSetParam) parameter{};
    HANDLE handle{INVALID_HANDLE_VALUE};
    explicit Divert(const std::filesystem::path& path) {
        module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) throw std::runtime_error(windows_error("Verified DLL loading", GetLastError()));
        try {
            open = function<decltype(open)>(module, "WinDivertOpen"); receive = function<decltype(receive)>(module, "WinDivertRecvEx");
            send = function<decltype(send)>(module, "WinDivertSend"); shutdown = function<decltype(shutdown)>(module, "WinDivertShutdown");
            close = function<decltype(close)>(module, "WinDivertClose"); parameter = function<decltype(parameter)>(module, "WinDivertSetParam");
        } catch (...) { FreeLibrary(module); module = nullptr; throw; }
    }
    ~Divert() { if (handle != INVALID_HANDLE_VALUE) close(handle); if (module) FreeLibrary(module); }
};
Handle parent_handle(DWORD expected) {
    Handle parent(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, expected));
    if (!parent.get()) throw std::runtime_error(windows_error("Owned parent opening", GetLastError()));
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (snapshot.get() == INVALID_HANDLE_VALUE) throw std::runtime_error("Parent identity snapshot failed.");
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry); bool matched = false;
    if (Process32FirstW(snapshot.get(), &entry)) do {
        if (entry.th32ProcessID == GetCurrentProcessId()) { matched = entry.th32ParentProcessID == expected; break; }
    } while (Process32NextW(snapshot.get(), &entry));
    if (!matched || WaitForSingleObject(parent.get(), 0) != WAIT_TIMEOUT) throw std::runtime_error("The declared owner is not the live direct parent.");
    return parent;
}
class ConsoleCancellation {
public:
    explicit ConsoleCancellation(HANDLE stop) {
        control_stop = stop;
        if (!SetConsoleCtrlHandler(console_control, TRUE)) { control_stop = nullptr; throw std::runtime_error("Control handler initialization failed."); }
    }
    ~ConsoleCancellation() { SetConsoleCtrlHandler(console_control, FALSE); control_stop = nullptr; }
};
class Control {
public:
    explicit Control(HANDLE stop) : stop_(stop) {
        const auto input = GetStdHandle(STD_INPUT_HANDLE);
        if (!input || input == INVALID_HANDLE_VALUE || GetFileType(input) != FILE_TYPE_PIPE) throw std::runtime_error("Owned stdin pipe is required.");
        worker_ = std::thread([input, stop] {
            std::string command; DWORD count{}; char byte{};
            while (WaitForSingleObject(stop, 0) == WAIT_TIMEOUT) {
                DWORD available{};
                if (!PeekNamedPipe(input, nullptr, 0, nullptr, &available, nullptr)) break;
                if (!available) { WaitForSingleObject(stop, 50); continue; }
                if (!ReadFile(input, &byte, 1, &count, nullptr) || count != 1) break;
                if (byte == '\r') continue;
                if (byte == '\n') { if (command == "STOP") break; command.clear(); }
                else if (command.size() < 64) command += byte;
                else break; // refuse unbounded control data
            }
            SetEvent(stop); // STOP, owner pipe closure or read failure all cancel.
        });
    }
    ~Control() {
        SetEvent(stop_);
        if (worker_.joinable()) worker_.join();
    }
private: HANDLE stop_; std::thread worker_;
};
class ResourceSampler {
public:
    std::string sample(const Metrics& metrics) {
        std::lock_guard guard(mutex_);
        FILETIME creation{}, exit{}, kernel{}, user{}; PROCESS_MEMORY_COUNTERS_EX memory{};
        memory.cb = sizeof(memory);
        const auto ticks = [](FILETIME value) { return (static_cast<std::uint64_t>(value.dwHighDateTime) << 32) | value.dwLowDateTime; };
        double cpu{}; const auto now = monotonic_ns();
        if (GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user)) {
            const auto current = ticks(kernel) + ticks(user);
            if (last_time_ && now > last_time_) cpu = std::clamp(static_cast<double>(current - last_cpu_) * 10000.0 /
                static_cast<double>(now - last_time_) / processors_, 0.0, 100.0);
            last_cpu_ = current; last_time_ = now;
        }
        const bool available = GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory)) != FALSE;
        return metrics.line(cpu, available ? static_cast<std::uint64_t>(memory.PrivateUsage) : 0);
    }
private:
    std::mutex mutex_;
    DWORD processors_ = std::max<DWORD>(1, GetActiveProcessorCount(ALL_PROCESSOR_GROUPS));
    std::uint64_t last_time_{}, last_cpu_{};
};
// Separate lower-priority sniff-only handle observes the actual post-injection
// lab packets. It never consumes traffic and stores header/length traces only.
class LabTrace {
public:
    LabTrace(Divert& divert, const Options& options, HANDLE stop, Metrics& metrics, std::mutex& output)
        : divert_(divert), stop_(stop), output_(output) {
        auto filter = options.filter(); const auto marker = filter.find("!impostor");
        if (marker != std::string::npos) filter.replace(marker, 9, "true"); // read-only observer must see reinjected lab packets too
        handle_ = divert.open(filter.c_str(), WINDIVERT_LAYER_NETWORK, -100, WINDIVERT_FLAG_SNIFF | WINDIVERT_FLAG_RECV_ONLY);
        if (handle_ == INVALID_HANDLE_VALUE) throw std::runtime_error(windows_error("Lab trace opening", GetLastError()));
        try {
            for (const auto& [name, value] : std::array<std::pair<WINDIVERT_PARAM, UINT64>, 3>{ {{WINDIVERT_PARAM_QUEUE_LENGTH, 128}, {WINDIVERT_PARAM_QUEUE_SIZE, 262144}, {WINDIVERT_PARAM_QUEUE_TIME, 500}} })
                if (!divert.parameter(handle_, name, value)) throw std::runtime_error(windows_error("Bounded lab trace queue", GetLastError()));
            worker_ = std::thread([this, &metrics, port = options.port] {
            try {
                Handle ready(CreateEventW(nullptr, TRUE, FALSE, nullptr));
                if (!ready.get()) throw std::runtime_error("Lab trace event unavailable.");
                std::array<std::uint8_t, MaximumPacketSize> bytes{};
                while (WaitForSingleObject(stop_, 0) == WAIT_TIMEOUT) {
                    WINDIVERT_ADDRESS address{}; UINT count{}, size = sizeof(address); OVERLAPPED io{}; io.hEvent = ready.get(); ResetEvent(ready.get());
                    auto received = divert_.receive(handle_, bytes.data(), static_cast<UINT>(bytes.size()), &count, 0, &address, &size, &io);
                    if (!received && GetLastError() == ERROR_IO_PENDING) {
                        const HANDLE waits[]{ready.get(), stop_}; const auto event = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
                        if (event != WAIT_OBJECT_0) { CancelIoEx(handle_, &io); DWORD ignored{}; GetOverlappedResult(handle_, &io, &ignored, TRUE); break; }
                        DWORD transferred{}; received = GetOverlappedResult(handle_, &io, &transferred, FALSE); count = transferred;
                    }
                    if (!received || count > bytes.size() || size != sizeof(address)) throw std::runtime_error("Lab trace receive failed.");
                    const auto packet = std::span(bytes).first(count); const auto view = classify(packet);
                    if (view.state == ParseState::Parsed && view.transport == Transport::Tcp && view.destination.port == port && !view.payload.empty()) {
                        ++observed_;
                        if (observed_ <= 256) { std::lock_guard guard(output_);
                            const PacketMetadata metadata{true, true, false, address.IPv6 != 0, true, true, false};
                            const auto lab_checksum = observe_split_input_checksum(packet, metadata);
                            std::cout << "NORTHPASS_LAB_TRACE stage=post seq=" << view.sequence << " ack=" << view.acknowledgement
                                << " bytes=" << count << " payload=" << view.payload.size() << " flags=" << static_cast<unsigned>(view.tcp_flags)
                                << " hello=" << simulator_recognizes_client_hello(packet)
                                << " checksum_valid=" << (observe_checksum(packet, metadata) == ChecksumObservation::Valid)
                                << " checksum_unverified=" << (lab_checksum == ChecksumObservation::OffloadUnverified)
                                << " ip_field=" << (view.ip_version == 4 ? (static_cast<unsigned>(packet[10]) << 8) | packet[11] : 0)
                                << " tcp_field=" << ((static_cast<unsigned>(packet[view.transport_offset + 16]) << 8) | packet[view.transport_offset + 17]) << '\n' << std::flush;
                        }
                    }
                }
            } catch (...) { ++metrics.fatal; SetEvent(stop_); }
        }); } catch (...) { divert_.close(handle_); handle_ = INVALID_HANDLE_VALUE; throw; }
    }
    bool stop() {
        SetEvent(stop_); if (worker_.joinable()) worker_.join();
        if (handle_ != INVALID_HANDLE_VALUE) { if (!divert_.close(handle_)) return false; handle_ = INVALID_HANDLE_VALUE; }
        return true;
    }
    ~LabTrace() { (void)stop(); }
    std::uint64_t observed() const { return observed_.load(); }
private:
    Divert& divert_; HANDLE stop_, handle_{INVALID_HANDLE_VALUE}; std::mutex& output_; std::thread worker_;
    std::atomic<std::uint64_t> observed_{};
};
int run(const Options& options) {
    auto locks = verify_runtime(executable_path());
    if (options.check) {
        Divert checked(executable_path().parent_path() / L"WinDivert.dll");
        std::cout << "NORTHPASS_CHECK protocol=1 mode=" << (options.loopback ? "loopback" : "idle") << '\n'; return 0;
    }
    auto parent = parent_handle(options.parent_pid);
    // Shared with v0.1: direct older/newer workers cannot overlap interception.
    Handle singleton(CreateMutexW(nullptr, FALSE, L"Global\\Northpass.Native.v0.1"));
    if (!singleton.get()) throw std::runtime_error(windows_error("Native ownership mutex", GetLastError()));
    const auto ownership = WaitForSingleObject(singleton.get(), 0);
    if (ownership != WAIT_OBJECT_0 && ownership != WAIT_ABANDONED) throw std::runtime_error("Another native session owns interception. Close it through its owner.");
    struct MutexRelease { HANDLE value; ~MutexRelease() { ReleaseMutex(value); } } release{singleton.get()};
    Handle stop(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!stop.get()) throw std::runtime_error("Cancellation event initialization failed.");
    ConsoleCancellation console(stop.get());
    Metrics metrics; ResourceSampler resources;
    (void)resources.sample(metrics); // prime CPU delta before even a brief lab session
    // Preallocate all queue storage before opening a driver/filter.
    PacketQueue queue(Metrics::queue_capacity);
    std::unique_ptr<Control> stdio;
    std::unique_ptr<PipeControl> ipc;
    if (options.pipe_id.empty()) stdio = std::make_unique<Control>(stop.get());
    else ipc = std::make_unique<PipeControl>(options.pipe_id, parent.get(), options.parent_pid, stop.get(), metrics,
        [&] { return resources.sample(metrics); });
    reduce_worker_privileges();
    Divert divert(executable_path().parent_path() / L"WinDivert.dll");
    const auto filter = options.filter();
    if (options.fault == TestFault::DriverOpen) throw std::runtime_error(windows_error("Injected scoped driver initialization failure", ERROR_GEN_FAILURE));
    divert.handle = divert.open(filter.c_str(), WINDIVERT_LAYER_NETWORK, 0, 0);
    if (divert.handle == INVALID_HANDLE_VALUE) throw std::runtime_error(windows_error("Scoped driver initialization", GetLastError()));
    for (const auto& [name, value] : std::array<std::pair<WINDIVERT_PARAM, UINT64>, 3>{ {{WINDIVERT_PARAM_QUEUE_LENGTH, 512}, {WINDIVERT_PARAM_QUEUE_SIZE, 1048576}, {WINDIVERT_PARAM_QUEUE_TIME, 1000}} })
        if (!divert.parameter(divert.handle, name, value)) throw std::runtime_error(windows_error("Bounded driver queue setup", GetLastError()));
    std::mutex send_mutex, error_mutex, output_mutex; std::string receive_error;
    NorthpassSplit split; FlowTracker split_flows(64);
    if (options.lab_split) split.initialize({4, 1280, 9, TransformScope::Laboratory});
    std::uint64_t proposed{}, accepted{}, rejected{}, lab_reinjections{}, send_failures{}, split_latency_ns{};
    std::atomic<std::uint64_t> lab_observation_errors{};
    std::unique_ptr<LabTrace> trace;
    if (options.lab_split) trace = std::make_unique<LabTrace>(divert, options, stop.get(), metrics, output_mutex);
    // This timer cannot be extended through commands or auto-recovery.
    std::thread hard_stop;
    if (options.lab_split) hard_stop = std::thread([&] { if (WaitForSingleObject(stop.get(), options.lab_seconds * 1000) == WAIT_TIMEOUT) SetEvent(stop.get()); });
    struct TimerCleanup { HANDLE stop; std::thread& thread; ~TimerCleanup() { SetEvent(stop); if (thread.joinable()) thread.join(); } } timer_cleanup{stop.get(), hard_stop};
    std::uint64_t checksum_invalid{}, offload_unverified{}, metadata_invalid{}, oversize{};
    bool send_fault_used = false;
    const auto forward = [&](const QueuedPacket& packet) {
        WINDIVERT_ADDRESS address{}; static_assert(sizeof(address) <= 80);
        std::memcpy(&address, packet.metadata.data(), sizeof(address)); UINT sent{};
        std::lock_guard guard(send_mutex);
        if (options.fault == TestFault::Send && !send_fault_used) {
            send_fault_used = true; ++metrics.dropped_known; ++metrics.fatal; SetEvent(stop.get()); return false;
        }
        if (!divert.send(divert.handle, packet.bytes.data(), static_cast<UINT>(packet.length), &sent, &address) || sent != packet.length) {
            ++metrics.dropped_known; ++metrics.fatal; SetEvent(stop.get()); return false;
        }
        ++metrics.forwarded; return true;
    };
    const auto lab_forward = [&](const QueuedPacket& packet, WINDIVERT_ADDRESS address, bool& transmission_attempted) {
        const auto bytes = std::span(packet.bytes).first(packet.length); const auto view = classify(bytes);
        // Runtime scope defense in addition to the driver filter; no ordinary
        // engine/broker command can request this experimental path.
        if (!options.lab_split || WaitForSingleObject(stop.get(), 0) != WAIT_TIMEOUT || !address.Outbound || !address.Loopback || address.Impostor)
            return forward(packet);
        Endpoint expected; expected.address[15] = 1; if (view.ip_version == 4) expected.address[12] = 127;
        if (view.state != ParseState::Parsed || view.transport != Transport::Tcp || view.source.address != expected.address || view.destination.address != expected.address)
            return forward(packet);
        const auto* flow = split_flows.observe(view, packet.length, Clock::now());
        if (view.destination.port != options.port || view.payload.empty() || view.payload[0] != 22) return forward(packet);
        const auto started = monotonic_ns(); ++proposed;
        const auto checksum = observe_split_input_checksum(bytes, {true, true, false, address.IPv6 != 0, address.IPChecksum != 0, address.TCPChecksum != 0, address.UDPChecksum != 0});
        if (checksum == ChecksumObservation::Invalid || checksum == ChecksumObservation::Unsupported) {
            ++rejected;
            if (proposed <= 64) { std::lock_guard guard(output_mutex); std::cout << "NORTHPASS_LAB_REJECT reason=checksum ip_field="
                << (view.ip_version == 4 ? (static_cast<unsigned>(bytes[10]) << 8) | bytes[11] : 0)
                << " tcp_field=" << ((static_cast<unsigned>(bytes[view.transport_offset + 16]) << 8) | bytes[view.transport_offset + 17]) << '\n' << std::flush; }
            return forward(packet);
        }
        auto decision = split.propose(bytes, flow);
        if (!decision.proposal) { ++rejected; return forward(packet); }
        SegmentTransaction transaction(bytes, split.transaction_configuration());
        if (!transaction.propose(*decision.proposal) || !transaction.commit()) { ++rejected; return forward(packet); }
        if (WaitForSingleObject(stop.get(), 0) != WAIT_TIMEOUT) { ++rejected; return forward(packet); }
        ++accepted; split_latency_ns += monotonic_ns() - started;
        if (accepted <= 64) { std::lock_guard guard(output_mutex);
            std::cout << "NORTHPASS_LAB_TRACE stage=original seq=" << view.sequence << " ack=" << view.acknowledgement
                << " bytes=" << bytes.size() << " payload=" << view.payload.size() << " flags=" << static_cast<unsigned>(view.tcp_flags)
                << " hello=1 segments=" << transaction.segments().size() << " retransmission=" << decision.retransmission << '\n' << std::flush;
        }
        address.IPChecksum = address.TCPChecksum = 1;
        std::lock_guard send_guard(send_mutex);
        for (std::size_t i = 0; i < transaction.segments().size(); ++i) {
            const auto& segment = transaction.segments()[i]; UINT sent{};
            transmission_attempted = true; // includes an ambiguous FIRST send failure
            const bool injected = options.lab_send_failure == i + 1;
            const bool delivered = !injected && divert.send(divert.handle, segment.data(), static_cast<UINT>(segment.size()), &sent, &address);
            const auto code = injected ? ERROR_GEN_FAILURE : !delivered ? GetLastError() : sent != segment.size() ? ERROR_WRITE_FAULT : ERROR_SUCCESS;
            if (code != ERROR_SUCCESS) {
                transaction.send_failed(); ++send_failures; ++metrics.dropped_known; ++metrics.fatal; SetEvent(stop.get());
                { std::lock_guard guard(error_mutex); receive_error = windows_error(injected ? "Injected Split lab reinjection" : "Split lab reinjection", code) + " No rollback after attempted send; connection may be interrupted."; }
                { std::lock_guard guard(output_mutex); std::cout << "NORTHPASS_LAB_SEND_FAILURE transmitted=" << transaction.sent()
                    << " unsent_segments=" << transaction.segments().size() - transaction.sent()
                    << " rollback_allowed=0 connection_may_be_interrupted=1\n" << std::flush; }
                return false; // never forward original after ambiguous/partial transmission
            }
            if (!transaction.record_sent(i)) throw std::runtime_error("Lab transaction send accounting failed.");
            ++lab_reinjections;
            if (accepted <= 64) { std::lock_guard guard(output_mutex); const auto sent_view = classify(segment);
                std::cout << "NORTHPASS_LAB_TRACE stage=sent seq=" << sent_view.sequence << " ack=" << sent_view.acknowledgement
                    << " bytes=" << segment.size() << " payload=" << sent_view.payload.size() << " flags=" << static_cast<unsigned>(sent_view.tcp_flags)
                    << " hello=" << simulator_recognizes_client_hello(segment) << " checksum_valid=1\n" << std::flush; }
        }
        ++metrics.forwarded; return true;
    };
    std::thread receiver([&] {
        try {
            Handle ready(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!ready.get()) throw std::runtime_error("Receive event initialization failed.");
            QueuedPacket packet; bool draining = false; ULONGLONG deadline{};
            const auto begin_drain = [&] {
                if (!draining) {
                    if (!divert.shutdown(divert.handle, WINDIVERT_SHUTDOWN_RECV)) throw std::runtime_error(windows_error("Receive shutdown", GetLastError()));
                    draining = true; deadline = GetTickCount64() + 3000;
                }
            };
            for (;;) {
                if (WaitForSingleObject(stop.get(), 0) == WAIT_OBJECT_0 || WaitForSingleObject(parent.get(), 0) == WAIT_OBJECT_0) begin_drain();
                if (draining && GetTickCount64() >= deadline) throw std::runtime_error("Packet drain timed out; kernel loss is unknown.");
                WINDIVERT_ADDRESS address{}; UINT length{}, address_length = sizeof(address);
                OVERLAPPED operation{}; operation.hEvent = ready.get(); ResetEvent(ready.get());
                struct PendingOperation {
                    HANDLE handle; OVERLAPPED& operation; bool pending{};
                    ~PendingOperation() { if (pending) { CancelIoEx(handle, &operation); DWORD ignored{}; GetOverlappedResult(handle, &operation, &ignored, TRUE); } }
                } pending{divert.handle, operation};
                BOOL received = divert.receive(divert.handle, packet.bytes.data(), static_cast<UINT>(packet.bytes.size()), &length, 0, &address, &address_length, &operation);
                DWORD code = received ? ERROR_SUCCESS : GetLastError();
                if (!received && code == ERROR_IO_PENDING) {
                    pending.pending = true;
                    for (;;) {
                        const HANDLE waits[]{ready.get(), stop.get(), parent.get()};
                        const auto timeout = draining ? static_cast<DWORD>(deadline > GetTickCount64() ? deadline - GetTickCount64() : 0) : INFINITE;
                        const auto event = WaitForMultipleObjects(draining ? 1 : 3, waits, FALSE, timeout);
                        if (event == WAIT_OBJECT_0) break;
                        if (event == WAIT_OBJECT_0 + 1 || event == WAIT_OBJECT_0 + 2) { begin_drain(); continue; }
                        throw std::runtime_error("Receive cancellation/drain failed; kernel loss is unknown.");
                    }
                    DWORD transferred{}; received = GetOverlappedResult(divert.handle, &operation, &transferred, FALSE);
                    pending.pending = false; length = transferred; code = received ? ERROR_SUCCESS : GetLastError();
                }
                if (!received) { if (draining && code == ERROR_NO_DATA) break; throw std::runtime_error(windows_error("Packet receive", code)); }
                ++metrics.captured;
                if (length == 0 || length > packet.bytes.size() || address_length != sizeof(address)) {
                    ++metrics.dropped_known; throw std::runtime_error("Unexpected driver framing; captured packet could not be reinjected.");
                }
                packet.length = length; packet.captured_ns = monotonic_ns(); std::memcpy(packet.metadata.data(), &address, sizeof(address));
                if (options.fault == TestFault::Crash) TerminateProcess(GetCurrentProcess(), 91);
                // Consumer/native pass-through is unchanged. Only explicit bounded
                // lab sessions may propose modifications BEFORE original forwarding.
                if (options.lab_split) {
                    const auto previous_proposed = proposed, previous_accepted = accepted;
                    bool transmission_attempted = false;
                    try { (void)lab_forward(packet, address, transmission_attempted); }
                    catch (...) {
                        if (transmission_attempted) { ++send_failures; ++metrics.dropped_known; ++metrics.fatal; SetEvent(stop.get()); }
                        else {
                            if (proposed > previous_proposed && accepted == previous_accepted) ++rejected;
                            ++lab_observation_errors; (void)forward(packet);
                        }
                    }
                } else (void)forward(packet);
                if (!queue.try_push(packet)) ++metrics.backpressure; // skip observation, never reorder originals or wait on space
                const auto size = queue.size(); metrics.queue_size = size;
                const auto actual_peak = queue.peak();
                auto peak = metrics.queue_peak.load(); while (peak < actual_peak && !metrics.queue_peak.compare_exchange_weak(peak, actual_peak)) { }
            }
        } catch (const std::exception& error) {
            ++metrics.fatal; { std::lock_guard guard(error_mutex); receive_error = error.what(); } SetEvent(stop.get());
        } catch (...) { ++metrics.fatal; SetEvent(stop.get()); }
        queue.close();
    });
    struct ReceiverCleanup { HANDLE stop; std::thread& thread; ~ReceiverCleanup() { SetEvent(stop); if (thread.joinable()) thread.join(); } } cleanup{stop.get(), receiver};
    PassThroughStrategy strategy; PacketProcessor processor(strategy);
    std::cout << "NORTHPASS_READY protocol=1\n" << std::flush;
    const auto report = [&] {
        std::lock_guard guard(output_mutex);
        std::cout << "NORTHPASS_STATS packets=" << metrics.captured.load() << " forwarded=" << metrics.forwarded.load() << " tcp=" << metrics.tcp.load()
            << " udp=" << metrics.udp.load() << " tls=" << metrics.tls.load() << " malformed=" << metrics.malformed.load()
            << " fragments=" << metrics.fragments.load() << " flows=" << metrics.active_flows.load() << '\n';
        std::cout << resources.sample(metrics) << '\n' << std::flush;
        std::cout << "NORTHPASS_METADATA checksum_invalid=" << checksum_invalid << " offload_unverified=" << offload_unverified
            << " metadata_invalid=" << metadata_invalid << " over_1500_bytes=" << oversize << '\n' << std::flush;
    };
    auto last_report = Clock::now(); QueuedPacket packet;
    while (!queue.drained()) {
        if (queue.pop(packet, std::chrono::milliseconds(100))) {
            if (options.fault == TestFault::ObserveDelay) WaitForSingleObject(stop.get(), 20);
            WINDIVERT_ADDRESS address{}; std::memcpy(&address, packet.metadata.data(), sizeof(address));
            const PacketMetadata metadata{address.Outbound != 0, address.Loopback != 0, address.Impostor != 0, address.IPv6 != 0,
                address.IPChecksum != 0, address.TCPChecksum != 0, address.UDPChecksum != 0};
            const auto bytes = std::span(packet.bytes).first(packet.length);
            if (!metadata_consistent(bytes, metadata)) ++metadata_invalid;
            const auto checksum = observe_checksum(bytes, metadata);
            if (checksum == ChecksumObservation::Invalid) ++checksum_invalid;
            if (checksum == ChecksumObservation::OffloadUnverified) ++offload_unverified;
            if (bytes.size() > 1500) ++oversize;
            (void)processor.process(std::span(packet.bytes).first(packet.length), Clock::now());
            metrics.record_latency(monotonic_ns() - packet.captured_ns);
        }
        processor.expire(Clock::now()); const auto& c = processor.counters();
        metrics.tcp = c.tcp; metrics.udp = c.udp; metrics.tls = c.tls; metrics.malformed = c.malformed; metrics.fragments = c.fragments;
        metrics.recoverable = c.recoverable_errors + lab_observation_errors.load(); metrics.rollbacks = c.rollbacks; metrics.active_flows = processor.flows(); metrics.queue_size = queue.size();
        if (Clock::now() - last_report >= std::chrono::seconds(1)) { report(); last_report = Clock::now(); }
    }
    receiver.join();
    if (trace && !trace->stop()) ++metrics.fatal;
    split.shutdown(); report();
    if (options.lab_split) {
        std::cout << "NORTHPASS_SPLIT_METRICS protocol=4 proposed=" << proposed << " accepted=" << accepted << " rejected=" << rejected
            << " lab_reinjections=" << lab_reinjections << " send_failures=" << send_failures << " dropped_known=" << metrics.dropped_known.load()
            << " kernel_loss_unknown=1 reconstruction_unknown=1 trace_packets=" << trace->observed()
            << " latency_us=" << (accepted ? static_cast<double>(split_latency_ns) / static_cast<double>(accepted) / 1000 : 0) << '\n' << std::flush;
    }
    if (!divert.close(divert.handle)) throw std::runtime_error(windows_error("Driver handle cleanup", GetLastError()));
    divert.handle = INVALID_HANDLE_VALUE;
    if (metrics.fatal.load() || metrics.dropped_known.load()) {
        std::lock_guard guard(error_mutex);
        throw std::runtime_error(receive_error.empty() ? "Native processing/control failed; inspect known drops and unknown kernel loss in metrics." : receive_error);
    }
    std::cout << "NORTHPASS_STOPPED protocol=1\n" << std::flush; return 0;
}
}
int main(int argc, char** argv) {
    try {
        std::vector<std::string_view> arguments;
        for (int i = 1; i < argc; ++i) arguments.emplace_back(argv[i]);
        const auto options = northpass::parse_options(arguments);
        if (options.version) { std::cout << "NorthpassCore 0.4.0 protocol=1\n"; return 0; }
        return run(options);
    } catch (const std::exception& error) { std::cerr << "NORTHPASS_ERROR " << error.what() << '\n'; return 1; }
}
