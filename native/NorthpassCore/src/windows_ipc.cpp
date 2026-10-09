#include "northpass/windows_ipc.hpp"
#include "northpass/ipc_protocol.hpp"
#include <sddl.h>
#include <array>
#include <algorithm>
#include <cstring>
#include <stdexcept>
#include <vector>
namespace northpass {
namespace {
std::wstring token_sid(HANDLE process, bool logon) {
    HANDLE raw{};
    if (!OpenProcessToken(process, TOKEN_QUERY, &raw)) throw std::runtime_error("IPC identity token unavailable.");
    Handle token(raw); DWORD size{};
    const auto kind = logon ? TokenGroups : TokenUser;
    GetTokenInformation(token.get(), kind, nullptr, 0, &size);
    if (!size || size > 65536) throw std::runtime_error("IPC identity token size invalid.");
    std::vector<std::uint8_t> bytes(size);
    if (!GetTokenInformation(token.get(), kind, bytes.data(), size, &size)) throw std::runtime_error("IPC identity query failed.");
    PSID sid{};
    if (!logon) sid = reinterpret_cast<TOKEN_USER*>(bytes.data())->User.Sid;
    else {
        const auto groups = reinterpret_cast<TOKEN_GROUPS*>(bytes.data());
        for (DWORD i = 0; i < groups->GroupCount; ++i)
            if ((groups->Groups[i].Attributes & SE_GROUP_LOGON_ID) == SE_GROUP_LOGON_ID) { sid = groups->Groups[i].Sid; break; }
    }
    LPWSTR text{};
    if (!sid || !ConvertSidToStringSidW(sid, &text)) throw std::runtime_error("IPC identity SID unavailable.");
    std::wstring result(text); LocalFree(text); return result;
}
bool peer_matches(HANDLE pipe, HANDLE parent, DWORD pid) {
    ULONG connected{};
    if (!GetNamedPipeClientProcessId(pipe, &connected) || connected != pid || WaitForSingleObject(parent, 0) != WAIT_TIMEOUT) return false;
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, connected));
    if (!process.get()) return false;
    try { return token_sid(process.get(), false) == token_sid(parent, false) && token_sid(process.get(), true) == token_sid(parent, true); }
    catch (...) { return false; }
}
// Every pending operation is completed/cancelled before its OVERLAPPED and buffer die.
void complete(HANDLE pipe, OVERLAPPED& operation, BOOL immediate, DWORD code, HANDLE stop, HANDLE parent, ULONGLONG deadline) {
    if (immediate || code == ERROR_PIPE_CONNECTED) return;
    if (code != ERROR_IO_PENDING) throw std::runtime_error(windows_error("IPC operation", code));
    const HANDLE events[]{operation.hEvent, stop, parent};
    const auto remaining = deadline > GetTickCount64() ? deadline - GetTickCount64() : 0;
    const auto wait = WaitForMultipleObjects(3, events, FALSE, static_cast<DWORD>(remaining));
    if (wait != WAIT_OBJECT_0) {
        CancelIoEx(pipe, &operation); DWORD ignored{}; GetOverlappedResult(pipe, &operation, &ignored, TRUE);
        throw std::runtime_error("IPC operation cancelled or timed out.");
    }
    DWORD transferred{};
    if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE)) throw std::runtime_error(windows_error("IPC completion", GetLastError()));
}
void transfer(HANDLE pipe, void* buffer, DWORD length, bool writing, HANDLE stop, HANDLE parent, ULONGLONG deadline) {
    DWORD position{};
    while (position < length) {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.get()) throw std::runtime_error("IPC event allocation failed.");
        OVERLAPPED operation{}; operation.hEvent = event.get(); DWORD count{};
        const auto pointer = static_cast<std::uint8_t*>(buffer) + position;
        const auto immediate = writing ? WriteFile(pipe, pointer, length - position, &count, &operation)
            : ReadFile(pipe, pointer, length - position, &count, &operation);
        const auto code = immediate ? ERROR_SUCCESS : GetLastError();
        complete(pipe, operation, immediate, code, stop, parent, deadline);
        if (!GetOverlappedResult(pipe, &operation, &count, FALSE) || !count || count > length - position) throw std::runtime_error("IPC frame was truncated.");
        position += count;
    }
}
std::string read_frame(HANDLE pipe, HANDLE stop, HANDLE parent, ULONGLONG deadline) {
    std::array<std::uint8_t, 4> header{}; transfer(pipe, header.data(), 4, false, stop, parent, deadline);
    const auto length = static_cast<std::uint32_t>(header[0]) | static_cast<std::uint32_t>(header[1]) << 8 |
        static_cast<std::uint32_t>(header[2]) << 16 | static_cast<std::uint32_t>(header[3]) << 24;
    if (!length || length > MaximumIpcMessage) throw std::runtime_error("IPC frame length rejected.");
    std::string result(length, '\0'); transfer(pipe, result.data(), length, false, stop, parent, deadline);
    for (const unsigned char byte : result) if (byte < 32 || byte > 126) throw std::runtime_error("IPC frame encoding rejected.");
    return result;
}
void write_frame(HANDLE pipe, std::string value, HANDLE stop, HANDLE parent) {
    if (value.empty() || value.size() > MaximumIpcMessage) throw std::runtime_error("IPC response exceeds bounds.");
    const auto length = static_cast<std::uint32_t>(value.size());
    std::array<std::uint8_t, 4> header{static_cast<std::uint8_t>(length), static_cast<std::uint8_t>(length >> 8),
        static_cast<std::uint8_t>(length >> 16), static_cast<std::uint8_t>(length >> 24)};
    const auto deadline = GetTickCount64() + 2000;
    transfer(pipe, header.data(), 4, true, stop, parent, deadline); transfer(pipe, value.data(), length, true, stop, parent, deadline);
}
std::string bootstrap_secret(HANDLE stop, HANDLE parent, ULONGLONG deadline) {
    const auto input = GetStdHandle(STD_INPUT_HANDLE);
    if (!input || input == INVALID_HANDLE_VALUE || GetFileType(input) != FILE_TYPE_PIPE) throw std::runtime_error("Private bootstrap pipe required.");
    std::string secret; secret.reserve(64);
    while (GetTickCount64() < deadline && WaitForSingleObject(stop, 0) == WAIT_TIMEOUT && WaitForSingleObject(parent, 0) == WAIT_TIMEOUT) {
        DWORD available{}, count{}; char byte{};
        if (!PeekNamedPipe(input, nullptr, 0, nullptr, &available, nullptr)) break;
        if (!available) { WaitForSingleObject(stop, 10); continue; }
        if (!ReadFile(input, &byte, 1, &count, nullptr) || count != 1) break;
        if (byte == '\r') continue;
        if (byte == '\n') { if (valid_nonce(secret)) return secret; break; }
        if (secret.size() >= 64) break;
        secret += byte;
    }
    throw std::runtime_error("Private IPC bootstrap rejected or timed out.");
}
}
void reduce_worker_privileges() {
    HANDLE raw{};
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &raw)) throw std::runtime_error("Worker token unavailable.");
    Handle token(raw);
    // Disable all token privileges. Administrators membership is retained for the
    // reviewed driver's SCM initialization; no debug/backup/restore privilege is used.
    if (!AdjustTokenPrivileges(token.get(), TRUE, nullptr, 0, nullptr, nullptr)) throw std::runtime_error("Worker privilege reduction failed.");
}
PipeControl::PipeControl(std::string_view id, HANDLE parent, DWORD parent_pid, HANDLE stop, Metrics& metrics,
    std::function<std::string()> snapshot) : parent_(parent), stop_(stop), metrics_(metrics) {
    if (!valid_pipe_id(id)) throw std::runtime_error("IPC identifier rejected.");
    const auto name = L"\\\\.\\pipe\\Northpass.Native." + std::wstring(id.begin(), id.end());
    // Individual file read/write/attribute/control-query rights, excluding
    // FILE_CREATE_PIPE_INSTANCE (4). User SID works for interactive and service
    // logons; peer_matches separately enforces the exact logon session and PID.
    // Mandatory medium label excludes low-integrity writers.
    const auto sddl = L"D:P(A;;GA;;;SY)(A;;0x0012019b;;;" + token_sid(parent, false) + L")S:(ML;;NW;;;ME)";
    PSECURITY_DESCRIPTOR descriptor{};
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr))
        throw std::runtime_error(windows_error("IPC ACL construction", GetLastError()));
    SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
    const auto raw = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 0, &attributes);
    LocalFree(descriptor);
    if (raw == INVALID_HANDLE_VALUE) throw std::runtime_error(windows_error("Exclusive authenticated IPC creation", GetLastError()));
    pipe_ = Handle(raw);
    auto secret = bootstrap_secret(stop, parent, GetTickCount64() + 10000);
    struct SecretWipe { std::string& value; ~SecretWipe() { SecureZeroMemory(value.data(), value.size()); } } wipe{secret};
    const auto deadline = GetTickCount64() + 10000; bool authenticated = false;
    for (int attempts = 0; attempts < 32 && GetTickCount64() < deadline; ++attempts) {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.get()) throw std::runtime_error("IPC connection event unavailable.");
        OVERLAPPED operation{}; operation.hEvent = event.get();
        const auto immediate = ConnectNamedPipe(pipe_.get(), &operation); const auto code = immediate ? ERROR_SUCCESS : GetLastError();
        complete(pipe_.get(), operation, immediate, code, stop, parent, deadline);
        try {
            if (peer_matches(pipe_.get(), parent, parent_pid) && authenticate(read_frame(pipe_.get(), stop, parent, std::min(deadline, GetTickCount64() + 2000)), secret)) {
                write_frame(pipe_.get(), "AUTH_OK 2", stop, parent); authenticated = true; break;
            }
        } catch (...) { if (WaitForSingleObject(stop, 0) != WAIT_TIMEOUT || WaitForSingleObject(parent, 0) != WAIT_TIMEOUT) throw; }
        ++metrics.auth_failures; DisconnectNamedPipe(pipe_.get());
    }
    SecureZeroMemory(secret.data(), secret.size());
    if (!authenticated) throw std::runtime_error("IPC owner authentication failed.");
    worker_ = std::thread([this, snapshot = std::move(snapshot)] {
        try {
            std::uint32_t expected = 1;
            while (WaitForSingleObject(stop_, 0) == WAIT_TIMEOUT && WaitForSingleObject(parent_, 0) == WAIT_TIMEOUT) {
                // Idle wait is cancellable; once a frame starts it has a 2s deadline.
                DWORD available{};
                if (!PeekNamedPipe(pipe_.get(), nullptr, 0, nullptr, &available, nullptr)) break;
                if (!available) { WaitForSingleObject(stop_, 20); continue; }
                const auto command = parse_command(read_frame(pipe_.get(), stop_, parent_, GetTickCount64() + 2000), expected);
                ++expected;
                write_frame(pipe_.get(), command.kind == CommandKind::Metrics ? snapshot() : "OK 2 " + std::to_string(command.sequence), stop_, parent_);
                if (command.kind == CommandKind::Stop) break;
            }
        } catch (...) { if (WaitForSingleObject(stop_, 0) == WAIT_TIMEOUT && WaitForSingleObject(parent_, 0) == WAIT_TIMEOUT) ++metrics_.fatal; }
        SetEvent(stop_);
    });
}
PipeControl::~PipeControl() { SetEvent(stop_); if (worker_.joinable()) worker_.join(); DisconnectNamedPipe(pipe_.get()); }
}
