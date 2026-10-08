#pragma once
#include "windows_security.hpp"
#include "metrics.hpp"
#include <functional>
#include <thread>
namespace northpass {
// Internal preview channel; production still uses the owned stdin protocol.
class PipeControl {
public:
    PipeControl(std::string_view id, HANDLE parent, DWORD parent_pid, HANDLE stop, Metrics& metrics,
        std::function<std::string()> snapshot);
    ~PipeControl();
    PipeControl(const PipeControl&) = delete;
    PipeControl& operator=(const PipeControl&) = delete;
private:
    Handle pipe_;
    HANDLE parent_, stop_;
    Metrics& metrics_;
    std::thread worker_;
};
void reduce_worker_privileges();
}
