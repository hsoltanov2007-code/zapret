#include "northpass/windows_security.hpp"
#include <aclapi.h>
#include <sddl.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <array>
#include <algorithm>
#include <memory>
#include <stdexcept>
namespace northpass {
namespace {
struct LocalFreeDeleter { void operator()(void* value) const { if (value) LocalFree(value); } };
using LocalMemory = std::unique_ptr<void, LocalFreeDeleter>;
bool trusted(PSID sid, bool parent) {
    for (const auto* text : { L"S-1-5-32-544", L"S-1-5-18", L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" }) {
        if (!parent && std::wstring_view(text).starts_with(L"S-1-5-80")) continue;
        PSID parsed{};
        if (!ConvertStringSidToSidW(text, &parsed)) throw std::runtime_error("Trusted SID conversion failed.");
        LocalMemory memory(parsed);
        if (EqualSid(parsed, sid)) return true;
    }
    return false;
}
void acl(const std::filesystem::path& path, bool parent = false) {
    const auto attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) throw std::runtime_error("Runtime path missing or contains a reparse point.");
    PSID owner{}; PACL dacl{}; PSECURITY_DESCRIPTOR descriptor{};
    auto object = path.wstring();
    const auto status = GetNamedSecurityInfoW(object.data(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
        &owner, nullptr, &dacl, nullptr, &descriptor);
    if (status != ERROR_SUCCESS) throw std::runtime_error(windows_error("ACL validation", status));
    LocalMemory memory(descriptor);
    SECURITY_DESCRIPTOR_CONTROL control{}; DWORD revision{};
    if (!owner || !trusted(owner, parent) || !dacl || !GetSecurityDescriptorControl(descriptor, &control, &revision) || (!parent && !(control & SE_DACL_PROTECTED)))
        throw std::runtime_error("Runtime ownership/ACL is unsafe. Repair installation; do not disable Windows security.");
    constexpr ACCESS_MASK write = FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES | DELETE | FILE_DELETE_CHILD | WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL;
    for (DWORD i = 0; i < dacl->AceCount; ++i) {
        void* raw{};
        if (!GetAce(dacl, i, &raw)) throw std::runtime_error("Invalid runtime ACL.");
        const auto* header = static_cast<ACE_HEADER*>(raw);
        if (header->AceFlags & INHERIT_ONLY_ACE) continue;
        if (header->AceType == ACCESS_ALLOWED_ACE_TYPE) {
            auto* ace = static_cast<ACCESS_ALLOWED_ACE*>(raw);
            if ((ace->Mask & write) && !trusted(&ace->SidStart, parent)) throw std::runtime_error("Untrusted write access to runtime files.");
        } else if (header->AceType != ACCESS_DENIED_ACE_TYPE) throw std::runtime_error("Unsupported runtime ACL; refusing uncertain permissions.");
    }
}
void verify_hash(HANDLE file, std::string_view expected, DWORD size) {
    LARGE_INTEGER length{};
    if (!GetFileSizeEx(file, &length) || length.QuadPart != size) throw std::runtime_error("Runtime component size mismatch.");
    BCRYPT_ALG_HANDLE provider{}; BCRYPT_HASH_HANDLE hash{};
    if (BCryptOpenAlgorithmProvider(&provider, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) throw std::runtime_error("SHA-256 provider unavailable.");
    struct Cleanup { BCRYPT_ALG_HANDLE p; BCRYPT_HASH_HANDLE* h; ~Cleanup() { if (*h) BCryptDestroyHash(*h); BCryptCloseAlgorithmProvider(p, 0); } } cleanup{provider, &hash};
    if (BCryptCreateHash(provider, &hash, nullptr, 0, nullptr, 0, 0) < 0) throw std::runtime_error("SHA-256 initialization failed.");
    std::array<UCHAR, 65536> buffer{}; DWORD count{};
    for (;;) {
        if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &count, nullptr)) throw std::runtime_error("Runtime component read failed.");
        if (count == 0) break;
        if (BCryptHashData(hash, buffer.data(), count, 0) < 0) throw std::runtime_error("SHA-256 update failed.");
    }
    std::array<UCHAR, 32> digest{};
    if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0) throw std::runtime_error("SHA-256 completion failed.");
    constexpr char hex[] = "0123456789abcdef";
    std::string actual; actual.reserve(64);
    for (auto byte : digest) { actual += hex[byte >> 4]; actual += hex[byte & 15]; }
    if (actual != expected) throw std::runtime_error("Runtime integrity mismatch. Repair installation; do not disable Windows security.");
}
}
std::filesystem::path executable_path() {
    std::array<wchar_t, 32768> buffer{};
    const auto size = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (!size || size >= buffer.size()) throw std::runtime_error("Executable path unavailable.");
    return std::wstring(buffer.data(), size);
}
std::string windows_error(const char* operation, DWORD code) {
    std::string help;
    if (code == ERROR_ACCESS_DENIED) help = " Administrator privileges are required; keep UAC enabled.";
    else if (code == ERROR_INVALID_IMAGE_HASH) help = " Windows rejected driver signing. Keep Secure Boot and signature enforcement enabled.";
    else if (code == ERROR_FILE_NOT_FOUND || code == ERROR_PATH_NOT_FOUND) help = " A verified component is missing or quarantined. Repair installation.";
    else if (code == ERROR_INVALID_PARAMETER) help = " The scoped interception filter or driver parameters are invalid.";
    return std::string(operation) + " failed (Windows code " + std::to_string(code) + ")." + help;
}
std::vector<Handle> verify_runtime(const std::filesystem::path& executable) {
    PWSTR folder{};
    if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramFiles, KF_FLAG_DEFAULT, nullptr, &folder))) throw std::runtime_error("Program Files location unavailable.");
    const std::filesystem::path program_files(folder); CoTaskMemFree(folder);
    const auto root = program_files / L"Northpass-Native";
    const auto directory = executable.parent_path();
    // Exact protected layout: Program Files/Northpass-Native/<payload id>/bin.
    if (directory.filename() != L"bin" || directory.parent_path().parent_path() != root) throw std::runtime_error("Native runtime must use its protected offline installation.");
    acl(program_files, true); acl(root); acl(directory.parent_path()); acl(directory);
    std::vector<Handle> locks;
    for (const auto* name : { L"NorthpassCore.exe", L"WinDivert.dll", L"WinDivert64.sys" }) {
        const auto path = directory / name; acl(path);
        Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (file.get() == INVALID_HANDLE_VALUE) throw std::runtime_error(windows_error("Runtime launch lease", GetLastError()));
        if (std::wstring_view(name) == L"WinDivert.dll") verify_hash(file.get(), "c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2", 47616);
        if (std::wstring_view(name) == L"WinDivert64.sys") verify_hash(file.get(), "8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2", 94144);
        locks.push_back(std::move(file));
    }
    return locks;
}
}
