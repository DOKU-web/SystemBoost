// The complete list of what the helper can do. Each command takes checked
// arguments and touches one fixed place: there is no generic "write this
// registry path" or "run this program", and a new ability has to be added here.
#include "commands.h"
#include "validate.h"
#include <windows.h>
#include <cstdio>

using sb::split;

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), nullptr, 0);
    if (n <= 0) return {};
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], n);
    return w;
}

std::string narrow(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

namespace {

const wchar_t kGraphics[] = L"SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers";
const wchar_t kIfeo[] = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\";
const wchar_t kUpdate[] = L"SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings";

std::string ok(const std::string& data = "") { return "ok\t" + data; }
std::string err(const std::string& why) { return "err\t" + why; }
std::string win(const char* what, LONG code) { return err(std::string(what) + " " + std::to_string(code)); }

std::string setDword(const std::wstring& key, const wchar_t* name, DWORD value) {
    HKEY k;
    LONG r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &k, nullptr);
    if (r != ERROR_SUCCESS) return win("open", r);
    r = RegSetValueExW(k, name, 0, REG_DWORD, (const BYTE*)&value, sizeof value);
    RegCloseKey(k);
    return r == ERROR_SUCCESS ? ok() : win("write", r);
}

std::string setString(const std::wstring& key, const std::wstring& name, const std::wstring& value) {
    HKEY k;
    LONG r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &k, nullptr);
    if (r != ERROR_SUCCESS) return win("open", r);
    r = RegSetValueExW(k, name.c_str(), 0, REG_SZ, (const BYTE*)value.c_str(), (DWORD)((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(k);
    return r == ERROR_SUCCESS ? ok() : win("write", r);
}

std::string deleteValue(const std::wstring& key, const std::wstring& name) {
    HKEY k;
    LONG r = RegOpenKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, KEY_SET_VALUE, &k);
    if (r == ERROR_FILE_NOT_FOUND) return ok();
    if (r != ERROR_SUCCESS) return win("open", r);
    r = RegDeleteValueW(k, name.c_str());
    RegCloseKey(k);
    return r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND ? ok() : win("delete", r);
}

// Removes a key only when nothing is left in it, so other programs' values survive.
void deleteIfEmpty(const std::wstring& key) {
    HKEY k;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, KEY_QUERY_VALUE, &k) != ERROR_SUCCESS) return;
    DWORD subkeys = 1, values = 1;
    LONG r = RegQueryInfoKeyW(k, nullptr, nullptr, nullptr, &subkeys, nullptr, nullptr, &values, nullptr, nullptr, nullptr, nullptr);
    RegCloseKey(k);
    if (r == ERROR_SUCCESS && subkeys == 0 && values == 0) RegDeleteKeyW(HKEY_LOCAL_MACHINE, key.c_str());
}

// ---------------------------------------------------------------- standby list
struct MemoryLists {
    ULONG_PTR Zero, Free, Modified, ModifiedNoWrite, Bad;
    ULONG_PTR StandbyByPriority[8];
    ULONG_PTR RepurposedByPriority[8];
    ULONG_PTR ModifiedPageFile;
};
const int kSystemMemoryListInformation = 80;
const int kMemoryPurgeStandbyList = 4;
using NtQuerySystemInformation_t = LONG(NTAPI*)(int, void*, ULONG, ULONG*);
using NtSetSystemInformation_t = LONG(NTAPI*)(int, void*, ULONG);

bool enablePrivilege(const wchar_t* name) {
    HANDLE token;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token)) return false;
    TOKEN_PRIVILEGES tp{};
    tp.PrivilegeCount = 1;
    tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    bool done = LookupPrivilegeValueW(nullptr, name, &tp.Privileges[0].Luid) &&
                AdjustTokenPrivileges(token, FALSE, &tp, 0, nullptr, nullptr) && GetLastError() == ERROR_SUCCESS;
    CloseHandle(token);
    return done;
}

bool memoryLists(unsigned long long& freeBytes, unsigned long long& standbyBytes) {
    auto query = (NtQuerySystemInformation_t)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation");
    MemoryLists m{};
    ULONG len = 0;
    if (!query || query(kSystemMemoryListInformation, &m, sizeof m, &len) < 0) return false;
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    unsigned long long standby = 0;
    for (ULONG_PTR pages : m.StandbyByPriority) standby += pages;
    freeBytes = (unsigned long long)(m.Zero + m.Free) * si.dwPageSize;
    standbyBytes = standby * si.dwPageSize;
    return true;
}

std::string ramStats() {
    if (!enablePrivilege(L"SeProfileSingleProcessPrivilege")) return err("privilege");
    unsigned long long f, s;
    if (!memoryLists(f, s)) return err("query");
    return ok(std::to_string(f) + ";" + std::to_string(s));
}

std::string ramPurge() {
    if (!enablePrivilege(L"SeProfileSingleProcessPrivilege")) return err("privilege");
    unsigned long long f0, s0, f1, s1;
    if (!memoryLists(f0, s0)) return err("query");
    auto set = (NtSetSystemInformation_t)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtSetSystemInformation");
    int command = kMemoryPurgeStandbyList;
    LONG status = set ? set(kSystemMemoryListInformation, &command, sizeof command) : -1;
    if (status < 0) { char b[32]; snprintf(b, sizeof b, "purge 0x%08lX", (unsigned long)status); return err(b); }
    if (!memoryLists(f1, s1)) return err("query");
    return ok(std::to_string(f0) + ";" + std::to_string(s0) + ";" + std::to_string(f1) + ";" + std::to_string(s1));
}

}  // namespace

std::string dispatch(const std::vector<std::string>& f) {
    const std::string& c = f[0];
    size_t n = f.size();

    if (c == "ping" && n == 1) return ok("pong");

    // hardware accelerated GPU scheduling
    if (c == "hags.set" && n == 2 && sb::isHagsMode(f[1])) return setDword(kGraphics, L"HwSchMode", (DWORD)std::stoi(f[1]));
    if (c == "hags.del" && n == 1) return deleteValue(kGraphics, L"HwSchMode");

    // start priority of a game: the OS applies it, nothing opens the game
    if (c == "ifeo.set" && n == 3 && sb::isExeName(f[1]) && sb::isPriorityClass(f[2]))
        return setDword(std::wstring(kIfeo) + widen(f[1]) + L"\\PerfOptions", L"CpuPriorityClass", (DWORD)std::stoi(f[2]));
    if (c == "ifeo.del" && n == 2 && sb::isExeName(f[1])) {
        std::wstring exe = std::wstring(kIfeo) + widen(f[1]);
        std::string r = deleteValue(exe + L"\\PerfOptions", L"CpuPriorityClass");
        deleteIfEmpty(exe + L"\\PerfOptions");
        deleteIfEmpty(exe);
        return r;
    }

    // standby memory
    if (c == "ram.stats" && n == 1) return ramStats();
    if (c == "ram.purge" && n == 1) return ramPurge();

    // network adapter power saving
    if (c == "net.pm.list" && n == 1) return netPowerList();
    if (c == "net.pm.set" && n == 3 && sb::isBit(f[2])) return netPowerSet(f[1], f[2] == "1");
    if (c == "net.top" && n == 1) return netTop();

    // Windows Update pause
    if (c == "wu.set" && n == 3 && sb::isWuName(f[1]) && sb::isIsoTime(f[2])) return setString(kUpdate, widen(f[1]), widen(f[2]));
    if (c == "wu.del" && n == 2 && sb::isWuName(f[1])) return deleteValue(kUpdate, widen(f[1]));

    // NVIDIA driver profile
    if (c == "nv.get" && n == 2 && sb::isExeName(f[1])) return nvGet(f[1]);
    if (c == "nv.set" && n == 2 && sb::isExeName(f[1])) return nvSet(f[1]);
    if (c == "nv.restore" && n == 4 && sb::isExeName(f[1]) && sb::isBit(f[2]) && sb::isOldValue(f[3]))
        return nvRestore(f[1], f[2] == "1", f[3]);

    return err("refused");
}
