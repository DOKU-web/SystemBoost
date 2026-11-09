// sb_helper: runs elevated for the admin-only boost features. The app starts it
// with one UAC prompt and it exits together with the app. A second copy runs plain
// for what needs no elevation, with the same checks.
//
// Security design:
//  - Pipe name is random per run and is created with FILE_FLAG_FIRST_PIPE_INSTANCE,
//    one instance, no remote clients, and a DACL for the app's user only.
//  - The caller must be the process that started us (--parent), and its image
//    must be SystemBoost.exe in this helper's own folder. Anything else is dropped.
//  - Requests are matched against the fixed list in commands.cpp with checked
//    arguments. Nothing here runs a program or writes a path chosen by the caller.
//  - Backups live in the app. What is kept here is the GPU tuning and the game's frame
//    limit, to undo if the app goes away. It never touches a game process, game memory
//    or game files.
#include "commands.h"
#include "validate.h"
#include <windows.h>
#include <sddl.h>
#include <shellapi.h>
#include <atomic>
#include <string>
#include <thread>

namespace {

const DWORD kConnectTimeoutMs = 30000;
const size_t kMaxLine = 4096;

std::wstring sidOf(HANDLE process) {
    HANDLE token;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return {};
    BYTE buffer[256];
    DWORD len = 0;
    std::wstring out;
    if (GetTokenInformation(token, TokenUser, buffer, sizeof buffer, &len)) {
        wchar_t* text = nullptr;
        if (ConvertSidToStringSidW(((TOKEN_USER*)buffer)->User.Sid, &text)) { out = text; LocalFree(text); }
    }
    CloseHandle(token);
    return out;
}

std::wstring expectedCaller() {
    wchar_t path[MAX_PATH * 2];
    DWORD n = GetModuleFileNameW(nullptr, path, ARRAYSIZE(path));
    std::wstring s(path, n);
    size_t slash = s.find_last_of(L'\\');
    return slash == std::wstring::npos ? L"" : s.substr(0, slash + 1) + L"SystemBoost.exe";
}

bool callerAllowed(HANDLE pipe, DWORD parent) {
    ULONG pid = 0;
    if (!GetNamedPipeClientProcessId(pipe, &pid) || pid != parent) return false;
    HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!p) return false;
    wchar_t image[MAX_PATH * 2];
    DWORD len = ARRAYSIZE(image);
    bool ok = QueryFullProcessImageNameW(p, 0, image, &len) != 0;
    CloseHandle(p);
    return ok && _wcsicmp(image, expectedCaller().c_str()) == 0;
}

[[noreturn]] void leave(UINT code) {
    ocShutdown();
    nvLimitShutdown();
    fpsShutdown();
    fpsLiveShutdown();
    ExitProcess(code);
}

bool sendLine(HANDLE pipe, std::string line) {
    line += '\n';
    DWORD written;
    return WriteFile(pipe, line.data(), (DWORD)line.size(), &written, nullptr) && written == line.size();
}

void serve(HANDLE pipe) {
    std::string buffer;
    char chunk[512];
    for (;;) {
        DWORD got = 0;
        if (!ReadFile(pipe, chunk, sizeof chunk, &got, nullptr) || got == 0) return;
        buffer.append(chunk, got);
        if (buffer.size() > kMaxLine) return;
        size_t end;
        while ((end = buffer.find('\n')) != std::string::npos) {
            std::string line = buffer.substr(0, end);
            buffer.erase(0, end + 1);
            if (!sendLine(pipe, dispatch(sb::split(line)))) return;
        }
    }
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc != 5 || wcscmp(argv[1], L"--pipe") != 0 || wcscmp(argv[3], L"--parent") != 0) return 2;
    std::string name = narrow(argv[2]);
    DWORD parentPid = (DWORD)wcstoul(argv[4], nullptr, 10);
    if (!sb::isHex(name, 32) || parentPid == 0) return 2;

    HANDLE parent = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, parentPid);
    if (!parent) return 3;
    std::thread([parent] { WaitForSingleObject(parent, INFINITE); leave(0); }).detach();

    std::wstring sid = sidOf(parent);
    if (sid.empty()) return 3;
    std::wstring sddl = L"D:P(A;;GRGW;;;" + sid + L")S:(ML;;NW;;;ME)";
    SECURITY_ATTRIBUTES sa{sizeof sa, nullptr, FALSE};
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sa.lpSecurityDescriptor, nullptr)) return 3;

    std::wstring pipeName = L"\\\\.\\pipe\\sb-helper-" + widen(name);
    HANDLE pipe = CreateNamedPipeW(pipeName.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                                   PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                                   1, 4096, 4096, 0, &sa);
    if (pipe == INVALID_HANDLE_VALUE) return 4;

    std::atomic<bool> connected{false};
    std::thread([&connected] {
        Sleep(kConnectTimeoutMs);
        if (!connected) ExitProcess(5);
    }).detach();

    if (!ConnectNamedPipe(pipe, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED) return 5;
    connected = true;
    if (!callerAllowed(pipe, parentPid)) return 6;
    serve(pipe);
    leave(0);
}
