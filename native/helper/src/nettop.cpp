// Bytes per process over TCP. Windows keeps no such count for programs, so this
// turns on the per-connection statistics (admin only) and adds them up by owner.
#include "commands.h"
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <tcpestats.h>
#include <map>
#include <vector>

namespace {

template <class Table> bool readTable(ULONG family, std::vector<char>& buffer) {
    DWORD size = 0;
    GetExtendedTcpTable(nullptr, &size, FALSE, family, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
    buffer.resize(size + 4096);
    size = (DWORD)buffer.size();
    return GetExtendedTcpTable(buffer.data(), &size, FALSE, family, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) == NO_ERROR;
}

void addIPv4(std::map<DWORD, unsigned long long>& totals) {
    std::vector<char> buffer;
    if (!readTable<MIB_TCPTABLE_OWNER_PID>(AF_INET, buffer)) return;
    auto* t = (MIB_TCPTABLE_OWNER_PID*)buffer.data();
    for (DWORD i = 0; i < t->dwNumEntries; i++) {
        const auto& e = t->table[i];
        if (e.dwState != MIB_TCP_STATE_ESTAB) continue;
        MIB_TCPROW row{};
        row.dwState = e.dwState;
        row.dwLocalAddr = e.dwLocalAddr;
        row.dwLocalPort = e.dwLocalPort;
        row.dwRemoteAddr = e.dwRemoteAddr;
        row.dwRemotePort = e.dwRemotePort;
        TCP_ESTATS_DATA_RW_v0 on{TRUE};
        SetPerTcpConnectionEStats(&row, TcpConnectionEstatsData, (PUCHAR)&on, 0, sizeof on, 0);
        TCP_ESTATS_DATA_ROD_v0 data{};
        if (GetPerTcpConnectionEStats(&row, TcpConnectionEstatsData, nullptr, 0, 0, nullptr, 0, 0, (PUCHAR)&data, 0, sizeof data) == NO_ERROR)
            totals[e.dwOwningPid] += data.DataBytesIn + data.DataBytesOut;
    }
}

void addIPv6(std::map<DWORD, unsigned long long>& totals) {
    std::vector<char> buffer;
    if (!readTable<MIB_TCP6TABLE_OWNER_PID>(AF_INET6, buffer)) return;
    auto* t = (MIB_TCP6TABLE_OWNER_PID*)buffer.data();
    for (DWORD i = 0; i < t->dwNumEntries; i++) {
        const auto& e = t->table[i];
        if (e.dwState != MIB_TCP_STATE_ESTAB) continue;
        MIB_TCP6ROW row{};
        row.State = MIB_TCP_STATE_ESTAB;
        memcpy(&row.LocalAddr, e.ucLocalAddr, 16);
        row.dwLocalScopeId = e.dwLocalScopeId;
        row.dwLocalPort = e.dwLocalPort;
        memcpy(&row.RemoteAddr, e.ucRemoteAddr, 16);
        row.dwRemoteScopeId = e.dwRemoteScopeId;
        row.dwRemotePort = e.dwRemotePort;
        TCP_ESTATS_DATA_RW_v0 on{TRUE};
        SetPerTcp6ConnectionEStats(&row, TcpConnectionEstatsData, (PUCHAR)&on, 0, sizeof on, 0);
        TCP_ESTATS_DATA_ROD_v0 data{};
        if (GetPerTcp6ConnectionEStats(&row, TcpConnectionEstatsData, nullptr, 0, 0, nullptr, 0, 0, (PUCHAR)&data, 0, sizeof data) == NO_ERROR)
            totals[e.dwOwningPid] += data.DataBytesIn + data.DataBytesOut;
    }
}

}  // namespace

// "ok\tPID=BYTES;PID=BYTES"
std::string netTop() {
    std::map<DWORD, unsigned long long> totals;
    addIPv4(totals);
    addIPv6(totals);
    std::string out;
    for (const auto& kv : totals) {
        if (kv.second == 0) continue;
        if (!out.empty()) out += ";";
        out += std::to_string(kv.first) + "=" + std::to_string(kv.second);
    }
    return "ok\t" + out;
}
