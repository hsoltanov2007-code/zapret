#include "northpass/windows_security.hpp"
#include "broker_components.hpp"
#include <shlobj.h>
#include <array>
#include <charconv>
#include <iostream>
#include <stdexcept>
#include <string>
#include <cstdio>
namespace {
using namespace northpass;
class BootstrapWindowsError: public std::runtime_error {
public: BootstrapWindowsError(const char* message,DWORD code):std::runtime_error(windows_error(message,code)),code_(code){}
    DWORD code()const noexcept{return code_;}
private: DWORD code_;
};
// One-way bounded evidence. The desktop's exclusive pipe is also a held
// liveness lease: its closure cancels this job even before worker authentication.
class BootstrapEvidence {
public:
    void open(std::wstring_view id,DWORD owner) {
        const auto name=L"\\\\.\\pipe\\Northpass.Broker."+std::wstring(id)+L".bootstrap-evidence";
        for(unsigned attempt=0;attempt<25;++attempt) {
            pipe_=Handle(CreateFileW(name.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr));
            if(pipe_.get()!=INVALID_HANDLE_VALUE)break;
            const auto error=GetLastError();if(error!=ERROR_FILE_NOT_FOUND && error!=ERROR_PIPE_BUSY)throw std::runtime_error("Bootstrap evidence lease unavailable.");
            Sleep(20);
        }
        ULONG pid{};
        if(pipe_.get()==INVALID_HANDLE_VALUE || !GetNamedPipeServerProcessId(pipe_.get(),&pid) || pid!=owner)throw std::runtime_error("Bootstrap evidence owner rejected.");
    }
    void report(unsigned stage,DWORD code=0,DWORD worker=0) noexcept {
        stage_=stage;
        if(!pipe_.get() || pipe_.get()==INVALID_HANDLE_VALUE || records_++>=32)return;
        std::array<char,128> line{};
        const auto length=std::snprintf(line.data(),line.size(),"NPB1 %u %llu %d %lu\n",stage,static_cast<unsigned long long>(GetTickCount64()-started_),static_cast<std::int32_t>(code),static_cast<unsigned long>(worker));
        if(length<=0 || static_cast<std::size_t>(length)>=line.size())return;
        Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));OVERLAPPED io{};io.hEvent=event.get();DWORD sent{};
        if(!event.get())return;
        if(!WriteFile(pipe_.get(),line.data(),static_cast<DWORD>(length),&sent,&io) && GetLastError()==ERROR_IO_PENDING) {
            if(WaitForSingleObject(event.get(),200)!=WAIT_OBJECT_0)CancelIoEx(pipe_.get(),&io);
            GetOverlappedResult(pipe_.get(),&io,&sent,TRUE);
        }
    }
    bool alive() const {
        DWORD available{};
        // No messages are accepted on this lease. Closure or unexpected input
        // can only stop the already owned job, never enable interception.
        return PeekNamedPipe(pipe_.get(),nullptr,0,nullptr,&available,nullptr)!=FALSE && available==0;
    }
    unsigned stage() const noexcept{return stage_;}
private: Handle pipe_;ULONGLONG started_=GetTickCount64();unsigned stage_=3,records_{};
};
BootstrapEvidence evidence;
std::uint64_t decimal(std::wstring_view value) {
    std::uint64_t result{};
    if(value.empty() || value.size()>20)throw std::runtime_error("Invalid broker identity value.");
    for(auto c:value){if(c<L'0'||c>L'9'||result>(UINT64_MAX-static_cast<unsigned>(c-L'0'))/10)throw std::runtime_error("Invalid broker identity value.");result=result*10+static_cast<unsigned>(c-L'0');}
    return result;
}
void cleanup_session(HANDLE job,const std::filesystem::path& program,std::wstring_view nonce) {
    // Only children in this held job and the fixed nonce-owned protected directory.
    // No user-supplied paths; never scan/delete other owners' active sessions.
    if(!TerminateJobObject(job,1))throw std::runtime_error("Owned broker job cleanup failed; packet loss is unknown.");
    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION state{};const auto deadline=GetTickCount64()+5000;
    for(;;) {
        if(!QueryInformationJobObject(job,JobObjectBasicAccountingInformation,&state,sizeof(state),nullptr))throw std::runtime_error("Owned broker job state unavailable.");
        if(state.ActiveProcesses==0)break;
        if(GetTickCount64()>=deadline)throw std::runtime_error("Owned broker job cleanup timed out; protected session retained.");
        Sleep(20);
    }
    const auto root=program/L"Northpass-BrokerData", session=root/(L"owner-"+std::wstring(nonce));
    if(!std::filesystem::exists(session))return;
    verify_protected_path(root);
    // A crash between creation and sealing may leave inherited safe permissions.
    // Trust checks still forbid untrusted writes/reparse points and unsafe owners.
    verify_protected_path(session,true);
    std::size_t entries{};
    for(auto it=std::filesystem::recursive_directory_iterator(session);it!=std::filesystem::recursive_directory_iterator();++it) {
        if(++entries>256 || it.depth()>8)throw std::runtime_error("Protected session cleanup exceeds bounds; retained for repair.");
        verify_protected_path(it->path(),true); // verify before traversal follows any directory
    }
    std::filesystem::remove_all(session);
}
int launch(int argc,wchar_t**argv) {
    const bool install=argc==2 && std::wstring_view(argv[1])==L"--install";
    if(!install && (argc<7 || argc>8 || std::wstring_view(argv[1])!=L"--owner" || std::wstring_view(argv[3])!=L"--created" || std::wstring_view(argv[5])!=L"--pipe"))throw std::runtime_error("Invalid broker bootstrap contract.");
    Handle owner;
    if(!install) {
        auto pid=decimal(argv[2]);auto created=decimal(argv[4]);const std::wstring_view id(argv[6]);
        if(!pid||pid>MAXDWORD||id.size()!=32)throw std::runtime_error("Invalid broker identity.");
        for(auto c:id)if(!((c>=L'0'&&c<=L'9')||(c>=L'a'&&c<=L'f')))throw std::runtime_error("Invalid broker identifier.");
        if(argc==8 && std::wstring_view(argv[7])!=L"--test-no-traffic")throw std::runtime_error("Unknown broker option.");
        evidence.open(id,static_cast<DWORD>(pid));evidence.report(3);
        owner=Handle(OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,static_cast<DWORD>(pid)));
        FILETIME creation{},exit{},kernel{},user{};
        if(!owner.get() || !GetProcessTimes(owner.get(),&creation,&exit,&kernel,&user) ||
            ((static_cast<std::uint64_t>(creation.dwHighDateTime)<<32)|creation.dwLowDateTime)!=created || WaitForSingleObject(owner.get(),0)!=WAIT_TIMEOUT)
            throw std::runtime_error("Broker desktop owner identity mismatch.");
    }
    evidence.report(4);
    const auto directory=executable_path().parent_path(), app=directory.parent_path();
    PWSTR raw{};if(FAILED(SHGetKnownFolderPath(FOLDERID_ProgramFiles,0,nullptr,&raw)))throw std::runtime_error("Program Files unavailable.");
    const std::filesystem::path program(raw);CoTaskMemFree(raw);
    if(directory.filename()!=L"broker" || app.parent_path()!=program)throw std::runtime_error("Broker must use its protected application installation.");
    verify_protected_path(program,true);verify_protected_path(app);verify_protected_path(directory);verify_protected_path(executable_path());
    std::vector<Handle> leases;
    for(const auto& component:broker_components) {
        auto path=directory/component.name;verify_protected_path(path);
        Handle file(CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr));
        if(file.get()==INVALID_HANDLE_VALUE)throw std::runtime_error("Broker worker file lease failed.");
        verify_component(file.get(),component.sha256,component.size);leases.push_back(std::move(file));
    }
    std::array<wchar_t,32768> system{};auto length=GetSystemDirectoryW(system.data(),static_cast<UINT>(system.size()));
    if(!length || length>=system.size())throw std::runtime_error("System directory unavailable.");
    const auto windows=std::filesystem::path(std::wstring(system.data(),length)).parent_path().wstring();
    std::vector<wchar_t> environment;
    for(const auto& item:std::array<std::wstring,3>{L"PATH="+std::wstring(system.data(),length),L"SystemRoot="+windows,L"WINDIR="+windows}) {
        environment.insert(environment.end(),item.begin(),item.end());environment.push_back(0);
    }
    environment.push_back(0); // no inherited CLR hooks/profilers, DOTNET_*, PATH or DLL overrides
    auto worker=directory/L"Northpass.Broker.Worker.exe";
    std::wstring command=L"\""+worker.wstring()+L"\"";
    for(int i=1;i<argc;++i){command+=L" ";command+=argv[i];} // all arguments individually validated, no shell
    Handle job(CreateJobObjectW(nullptr,nullptr));JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if(!job.get() || !SetInformationJobObject(job.get(),JobObjectExtendedLimitInformation,&limits,sizeof(limits)))throw std::runtime_error("Broker ownership job initialization failed.");
    STARTUPINFOW startup{};startup.cb=sizeof(startup);PROCESS_INFORMATION info{};
    evidence.report(5);
    if(!CreateProcessW(worker.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_SUSPENDED|CREATE_UNICODE_ENVIRONMENT|CREATE_NO_WINDOW,
        environment.data(),directory.c_str(),&startup,&info))throw BootstrapWindowsError("Verified broker worker creation",GetLastError());
    Handle process(info.hProcess),thread(info.hThread);
    if(!AssignProcessToJobObject(job.get(),process.get())){TerminateProcess(process.get(),1);WaitForSingleObject(process.get(),5000);throw std::runtime_error("Broker worker job assignment failed.");}
    if(ResumeThread(thread.get())==MAXDWORD)throw std::runtime_error("Broker worker resume failed.");
    if(!install)evidence.report(5,0,info.dwProcessId);
    try {
    if(install) {
        if(WaitForSingleObject(process.get(),120000)!=WAIT_OBJECT_0)throw std::runtime_error("Protected installation helper timed out.");
    } else {
        const HANDLE events[]{process.get(),owner.get()};DWORD event{};
        do {
            event=WaitForMultipleObjects(2,events,FALSE,25);
            if(event==WAIT_TIMEOUT && !evidence.alive()) {
                // Primary IPC closure normally lets the worker stop gracefully.
                // Before authentication it may still be waiting for a vanished
                // pipe. Give it a bounded grace, then close only our owned job.
                if(WaitForSingleObject(process.get(),2000)!=WAIT_OBJECT_0) {
                    evidence.report(13,ERROR_CANCELLED,info.dwProcessId);
                    cleanup_session(job.get(),program,argv[6]);
                    return 0; // explicit owner cancellation; kernel losses unknown
                }
                event=WAIT_OBJECT_0;
            }
        } while(event==WAIT_TIMEOUT);
        if(event==WAIT_OBJECT_0+1 && WaitForSingleObject(process.get(),10000)!=WAIT_OBJECT_0)
            throw std::runtime_error("Owner terminated; graceful broker cleanup timed out. Owned job will close; kernel loss is unknown.");
        if(event!=WAIT_OBJECT_0 && event!=WAIT_OBJECT_0+1)throw std::runtime_error("Broker ownership wait failed.");
    }
    DWORD code{};if(!GetExitCodeProcess(process.get(),&code))throw std::runtime_error("Broker worker exit status unavailable.");
    // Job closes after graceful worker exit; any orphan from a crash is owned and killed.
    if(!install)cleanup_session(job.get(),program,argv[6]);
    return static_cast<int>(code);
    } catch (...) {
        if(!install)try{cleanup_session(job.get(),program,argv[6]);}catch(const std::exception& e){std::cerr<<"NORTHPASS_BOOTSTRAP_CLEANUP_ERROR "<<e.what()<<'\n';}
        throw;
    }
}
}
int wmain(int argc,wchar_t**argv){try{return launch(argc,argv);}catch(const std::exception& e){const auto* windows=dynamic_cast<const BootstrapWindowsError*>(&e);evidence.report(evidence.stage(),windows?windows->code():ERROR_INVALID_DATA);std::cerr<<"NORTHPASS_BOOTSTRAP_ERROR "<<e.what()<<'\n';return static_cast<int>(0x4e510000U|(evidence.stage()<<8)|1U);}}
