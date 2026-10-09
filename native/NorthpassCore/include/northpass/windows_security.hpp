#pragma once
#include <windows.h>
#include <filesystem>
#include <string>
#include <vector>
namespace northpass {
class Handle {
public:
    explicit Handle(HANDLE value = nullptr) noexcept : value_(value) {}
    ~Handle() { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value_(other.value_) { other.value_ = nullptr; }
    Handle& operator=(Handle&& other) noexcept {
        if (this != &other) { if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); value_ = other.value_; other.value_ = nullptr; }
        return *this;
    }
    HANDLE get() const noexcept { return value_; }
private: HANDLE value_;
};
std::filesystem::path executable_path();
// Holds non-write/non-delete file handles for the complete native session.
std::vector<Handle> verify_runtime(const std::filesystem::path& executable);
std::string windows_error(const char* operation, DWORD code);
void verify_protected_path(const std::filesystem::path&, bool system_parent = false);
void verify_component(HANDLE, std::string_view hash, DWORD size);
}
