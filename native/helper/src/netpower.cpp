// "Allow the computer to turn off this device to save power" for physical
// network adapters (MSPower_DeviceEnable, the same value Device Manager shows).
// Only instances that belong to a physical adapter are listed or changed.
#include "commands.h"
#include <windows.h>
#include <wbemidl.h>
#include <algorithm>
#include <cwctype>
#include <vector>

namespace {

template <class T> struct Com {
    T* p = nullptr;
    ~Com() { if (p) p->Release(); }
    T** operator&() { return &p; }
    T* operator->() { return p; }
    explicit operator bool() const { return p != nullptr; }
};

struct Bstr {
    BSTR s;
    explicit Bstr(const wchar_t* t) : s(SysAllocString(t)) {}
    ~Bstr() { SysFreeString(s); }
    operator BSTR() const { return s; }
};

std::wstring upper(std::wstring s) {
    std::transform(s.begin(), s.end(), s.begin(), [](wchar_t c) { return (wchar_t)std::towupper(c); });
    return s;
}

struct Wmi {
    Com<IWbemLocator> locator;
    bool open() {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) return false;
        CoInitializeSecurity(nullptr, -1, nullptr, nullptr, RPC_C_AUTHN_LEVEL_DEFAULT, RPC_C_IMP_LEVEL_IMPERSONATE,
                             nullptr, EOAC_NONE, nullptr);
        return SUCCEEDED(CoCreateInstance(CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER, IID_IWbemLocator, (void**)&locator));
    }
    bool connect(const wchar_t* ns, IWbemServices** out) {
        Bstr name(ns);
        if (FAILED(locator->ConnectServer(name, nullptr, nullptr, nullptr, 0, nullptr, nullptr, out))) return false;
        CoSetProxyBlanket(*out, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, nullptr, RPC_C_AUTHN_LEVEL_CALL,
                          RPC_C_IMP_LEVEL_IMPERSONATE, nullptr, EOAC_NONE);
        return true;
    }
};

std::vector<std::wstring> physicalInstances(Wmi& wmi) {
    std::vector<std::wstring> out;
    Com<IWbemServices> cim;
    if (!wmi.connect(L"ROOT\\CIMV2", &cim)) return out;
    Bstr lang(L"WQL"), query(L"SELECT PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE");
    Com<IEnumWbemClassObject> rows;
    if (FAILED(cim->ExecQuery(lang, query, WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY, nullptr, &rows))) return out;
    for (;;) {
        Com<IWbemClassObject> row;
        ULONG got = 0;
        if (rows->Next(WBEM_INFINITE, 1, &row, &got) != S_OK || got == 0) break;
        VARIANT v;
        VariantInit(&v);
        if (SUCCEEDED(row->Get(L"PNPDeviceID", 0, &v, nullptr, nullptr)) && v.vt == VT_BSTR)
            out.push_back(upper(v.bstrVal) + L"_0");
        VariantClear(&v);
    }
    return out;
}

template <class F> bool forEachAdapter(F&& visit) {
    Wmi wmi;
    if (!wmi.open()) return false;
    auto physical = physicalInstances(wmi);
    Com<IWbemServices> svc;
    if (!wmi.connect(L"ROOT\\WMI", &svc)) return false;
    Bstr cls(L"MSPower_DeviceEnable");
    Com<IEnumWbemClassObject> rows;
    if (FAILED(svc->CreateInstanceEnum(cls, WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY, nullptr, &rows))) return false;
    for (;;) {
        Com<IWbemClassObject> row;
        ULONG got = 0;
        if (rows->Next(WBEM_INFINITE, 1, &row, &got) != S_OK || got == 0) break;
        VARIANT name, enable;
        VariantInit(&name);
        VariantInit(&enable);
        if (SUCCEEDED(row->Get(L"InstanceName", 0, &name, nullptr, nullptr)) && name.vt == VT_BSTR &&
            SUCCEEDED(row->Get(L"Enable", 0, &enable, nullptr, nullptr)) && enable.vt == VT_BOOL) {
            std::wstring id = upper(name.bstrVal);
            if (std::find(physical.begin(), physical.end(), id) != physical.end())
                visit(id, enable.boolVal != VARIANT_FALSE, svc.p, row.p);
        }
        VariantClear(&name);
        VariantClear(&enable);
    }
    return true;
}

}  // namespace

// "ok\tINSTANCE=1;INSTANCE=0"
std::string netPowerList() {
    std::string list;
    bool done = forEachAdapter([&](const std::wstring& id, bool on, IWbemServices*, IWbemClassObject*) {
        if (!list.empty()) list += ";";
        list += narrow(id) + "=" + (on ? "1" : "0");
    });
    return done ? "ok\t" + list : "err\twmi";
}

std::string netPowerSet(const std::string& instance, bool enable) {
    std::wstring want = upper(widen(instance));
    bool found = false, written = false;
    forEachAdapter([&](const std::wstring& id, bool, IWbemServices* svc, IWbemClassObject* row) {
        if (id != want) return;
        found = true;
        VARIANT v;
        VariantInit(&v);
        v.vt = VT_BOOL;
        v.boolVal = enable ? VARIANT_TRUE : VARIANT_FALSE;
        written = SUCCEEDED(row->Put(L"Enable", 0, &v, 0)) && SUCCEEDED(svc->PutInstance(row, WBEM_FLAG_UPDATE_ONLY, nullptr, nullptr));
    });
    if (!found) return "err\tnot a physical adapter";
    return written ? "ok\t" : "err\twrite";
}
