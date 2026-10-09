#include "northpass/flow.hpp"
#include "northpass/options.hpp"
#include "northpass/windows_security.hpp"
#include "northpass/windows_ipc.hpp"
#include "northpass/queue.hpp"
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
    divert.handle = divert.open(filter.c_str(), WINDIVERT_LAYER_NETWORK, 0, 0);
    if (divert.handle == INVALID_HANDLE_VALUE) throw std::runtime_error(windows_error("Scoped driver initialization", GetLastError()));
    for (const auto& [name, value] : std::array<std::pair<WINDIVERT_PARAM, UINT64>, 3>{ {{WINDIVERT_PARAM_QUEUE_LENGTH, 512}, {WINDIVERT_PARAM_QUEUE_SIZE, 1048576}, {WINDIVERT_PARAM_QUEUE_TIME, 1000}} })
        if (!divert.parameter(divert.handle, name, value)) throw std::runtime_error(windows_error("Bounded driver queue setup", GetLastError()));
    std::mutex send_mutex, error_mutex; std::string receive_error;
    const auto forward = [&](const QueuedPacket& packet) {
        WINDIVERT_ADDRESS address{}; static_assert(sizeof(address) <= 80);
        std::memcpy(&address, packet.metadata.data(), sizeof(address)); UINT sent{};
        std::lock_guard guard(send_mutex);
        if (!divert.send(divert.handle, packet.bytes.data(), static_cast<UINT>(packet.length), &sent, &address) || sent != packet.length) {
            ++metrics.dropped_known; ++metrics.fatal; SetEvent(stop.get()); return false;
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
                (void)forward(packet); // original bytes are reinjected before fallible/read-only observation
                if (!queue.try_push(packet)) ++metrics.backpressure; // skip observation, never reorder originals or wait on space
                const auto size = queue.size(); metrics.queue_size = size;
                auto peak = metrics.queue_peak.load(); while (peak < size && !metrics.queue_peak.compare_exchange_weak(peak, size)) { }
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
        std::cout << "NORTHPASS_STATS packets=" << metrics.captured.load() << " forwarded=" << metrics.forwarded.load() << " tcp=" << metrics.tcp.load()
            << " udp=" << metrics.udp.load() << " tls=" << metrics.tls.load() << " malformed=" << metrics.malformed.load()
            << " fragments=" << metrics.fragments.load() << " flows=" << metrics.active_flows.load() << '\n';
        std::cout << resources.sample(metrics) << '\n' << std::flush;
    };
    auto last_report = Clock::now(); QueuedPacket packet;
    while (!queue.drained()) {
        if (queue.pop(packet, std::chrono::milliseconds(100))) {
            (void)processor.process(std::span(packet.bytes).first(packet.length), Clock::now());
            metrics.record_latency(monotonic_ns() - packet.captured_ns);
        }
        processor.expire(Clock::now()); const auto& c = processor.counters();
        metrics.tcp = c.tcp; metrics.udp = c.udp; metrics.tls = c.tls; metrics.malformed = c.malformed; metrics.fragments = c.fragments;
        metrics.recoverable = c.recoverable_errors; metrics.rollbacks = c.rollbacks; metrics.active_flows = processor.flows(); metrics.queue_size = queue.size();
        if (Clock::now() - last_report >= std::chrono::seconds(1)) { report(); last_report = Clock::now(); }
    }
    receiver.join(); report();
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
        if (options.version) { std::cout << "NorthpassCore 0.2.0 protocol=1\n"; return 0; }
        return run(options);
    } catch (const std::exception& error) { std::cerr << "NORTHPASS_ERROR " << error.what() << '\n'; return 1; }
}
