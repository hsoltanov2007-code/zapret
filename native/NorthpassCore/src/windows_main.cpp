#include "northpass/flow.hpp"
#include "northpass/options.hpp"
#include "northpass/windows_security.hpp"
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
class Control {
public:
    explicit Control(HANDLE stop) : stop_(stop) {
        const auto input = GetStdHandle(STD_INPUT_HANDLE);
        if (!input || input == INVALID_HANDLE_VALUE || GetFileType(input) != FILE_TYPE_PIPE) throw std::runtime_error("Owned stdin pipe is required.");
        control_stop = stop;
        if (!SetConsoleCtrlHandler(console_control, TRUE)) { control_stop = nullptr; throw std::runtime_error("Control handler initialization failed."); }
        try { worker_ = std::thread([input, stop] {
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
        }); } catch (...) { SetConsoleCtrlHandler(console_control, FALSE); control_stop = nullptr; throw; }
    }
    ~Control() {
        SetConsoleCtrlHandler(console_control, FALSE); control_stop = nullptr;
        SetEvent(stop_);
        if (worker_.joinable()) worker_.join();
    }
private: HANDLE stop_; std::thread worker_;
};
int run(const Options& options) {
    auto locks = verify_runtime(executable_path());
    Divert divert(executable_path().parent_path() / L"WinDivert.dll");
    // Check-only validates protected bytes and API availability; no driver/filter is opened.
    if (options.check) { std::cout << "NORTHPASS_CHECK protocol=1 mode=" << (options.loopback ? "loopback" : "idle") << '\n'; return 0; }
    auto parent = parent_handle(options.parent_pid);
    Handle singleton(CreateMutexW(nullptr, FALSE, L"Global\\Northpass.Native.v0.1"));
    if (!singleton.get()) throw std::runtime_error(windows_error("Native ownership mutex", GetLastError()));
    const auto ownership = WaitForSingleObject(singleton.get(), 0);
    if (ownership != WAIT_OBJECT_0 && ownership != WAIT_ABANDONED) throw std::runtime_error("Another native session owns interception. Close it through its owner.");
    struct MutexRelease { HANDLE value; ~MutexRelease() { ReleaseMutex(value); } } release{singleton.get()};
    Handle stop(CreateEventW(nullptr, TRUE, FALSE, nullptr)), ready(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!stop.get() || !ready.get()) throw std::runtime_error("Cancellation event initialization failed.");
    Control control(stop.get());
    const auto filter = options.filter();
    divert.handle = divert.open(filter.c_str(), WINDIVERT_LAYER_NETWORK, 0, 0);
    if (divert.handle == INVALID_HANDLE_VALUE) throw std::runtime_error(windows_error("Scoped driver initialization", GetLastError()));
    for (const auto& [name, value] : std::array<std::pair<WINDIVERT_PARAM, UINT64>, 3>{ {{WINDIVERT_PARAM_QUEUE_LENGTH, 512}, {WINDIVERT_PARAM_QUEUE_SIZE, 1048576}, {WINDIVERT_PARAM_QUEUE_TIME, 1000}} })
        if (!divert.parameter(divert.handle, name, value)) throw std::runtime_error(windows_error("Bounded driver queue setup", GetLastError()));
    PassThroughStrategy strategy; PacketProcessor processor(strategy);
    std::vector<std::uint8_t> packet(WINDIVERT_MTU_MAX);
    std::uint64_t forwarded{}; bool draining = false; ULONGLONG deadline{};
    std::cout << "NORTHPASS_READY protocol=1\n" << std::flush;
    auto last_report = Clock::now();
    const auto report = [&] {
        const auto& c = processor.counters();
        std::cout << "NORTHPASS_STATS packets=" << c.packets << " forwarded=" << forwarded << " tcp=" << c.tcp << " udp=" << c.udp
            << " tls=" << c.tls << " malformed=" << c.malformed << " fragments=" << c.fragments << " flows=" << processor.flows() << '\n' << std::flush;
    };
    for (;;) {
        const auto begin_drain = [&] {
            if (!draining) {
                if (!divert.shutdown(divert.handle, WINDIVERT_SHUTDOWN_RECV)) throw std::runtime_error(windows_error("Receive shutdown", GetLastError()));
                draining = true; deadline = GetTickCount64() + 3000;
            }
        };
        if (WaitForSingleObject(stop.get(), 0) == WAIT_OBJECT_0 || WaitForSingleObject(parent.get(), 0) == WAIT_OBJECT_0) begin_drain();
        if (draining && GetTickCount64() >= deadline) throw std::runtime_error("Packet drain timed out; scoped queued packets may have been lost.");
        WINDIVERT_ADDRESS address{}; UINT length{}, address_length = sizeof(address);
        OVERLAPPED operation{}; operation.hEvent = ready.get(); ResetEvent(ready.get());
        struct PendingOperation {
            HANDLE handle; OVERLAPPED& operation; bool pending{};
            ~PendingOperation() {
                if (pending) { CancelIoEx(handle, &operation); DWORD ignored{}; GetOverlappedResult(handle, &operation, &ignored, TRUE); }
            }
        } pending{divert.handle, operation};
        BOOL received = divert.receive(divert.handle, packet.data(), static_cast<UINT>(packet.size()), &length, 0, &address, &address_length, &operation);
        DWORD code = received ? ERROR_SUCCESS : GetLastError();
        if (!received && code == ERROR_IO_PENDING) {
            pending.pending = true;
            for (;;) {
                const HANDLE waits[]{ready.get(), stop.get(), parent.get()};
                const auto timeout = draining ? static_cast<DWORD>(deadline > GetTickCount64() ? deadline - GetTickCount64() : 0) : INFINITE;
                const auto event = WaitForMultipleObjects(draining ? 1 : 3, waits, FALSE, timeout);
                if (event == WAIT_OBJECT_0) break;
                if (event == WAIT_OBJECT_0 + 1 || event == WAIT_OBJECT_0 + 2) { begin_drain(); continue; }
                throw std::runtime_error("Receive cancellation/drain failed; scoped queued packets may have been lost.");
            }
            DWORD transferred{};
            received = GetOverlappedResult(divert.handle, &operation, &transferred, FALSE);
            pending.pending = false;
            length = transferred; code = received ? ERROR_SUCCESS : GetLastError();
        }
        if (!received) {
            if (draining && code == ERROR_NO_DATA) break;
            throw std::runtime_error(windows_error("Packet receive", code));
        }
        if (length == 0 || length > packet.size() || address_length != sizeof(address)) throw std::runtime_error("Unexpected driver packet framing.");
        // Observation must not become a traffic rewrite. Even parser/strategy failure
        // forwards the original bytes before ending the experimental session.
        std::exception_ptr observation_error;
        try { (void)processor.process(std::span(packet).first(length), Clock::now()); }
        catch (...) { observation_error = std::current_exception(); }
        UINT sent{};
        if (!divert.send(divert.handle, packet.data(), length, &sent, &address) || sent != length)
            throw std::runtime_error(windows_error("Original packet reinjection (scoped packet may be lost)", GetLastError()));
        ++forwarded;
        if (observation_error) std::rethrow_exception(observation_error);
        if (Clock::now() - last_report >= std::chrono::seconds(1)) { report(); last_report = Clock::now(); }
    }
    report(); std::cout << "NORTHPASS_STOPPED protocol=1\n" << std::flush;
    return 0;
}
}
int main(int argc, char** argv) {
    try {
        std::vector<std::string_view> arguments;
        for (int i = 1; i < argc; ++i) arguments.emplace_back(argv[i]);
        const auto options = northpass::parse_options(arguments);
        if (options.version) { std::cout << "NorthpassCore 0.1.0 protocol=1\n"; return 0; }
        return run(options);
    } catch (const std::exception& error) { std::cerr << "NORTHPASS_ERROR " << error.what() << '\n'; return 1; }
}
