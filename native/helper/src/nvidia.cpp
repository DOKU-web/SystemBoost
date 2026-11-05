// Per-game "power management mode" through NVIDIA's public driver settings API.
// A game the driver does not know gets its own "SystemBoost - x.exe" profile,
// which is removed again on undo. In a profile that already exists only this
// one setting changes, and the old value goes back on undo.
#include "commands.h"
#include "nvapi.h"
#include "NvApiDriverSettings.h"
#include <string>

namespace {

const std::string kPrefix = "SystemBoost - ";

void toUnicode(const std::wstring& w, NvAPI_UnicodeString out) {
    size_t n = w.size() < NVAPI_UNICODE_STRING_MAX - 1 ? w.size() : NVAPI_UNICODE_STRING_MAX - 1;
    for (size_t i = 0; i < n; i++) out[i] = (NvU16)w[i];
    out[n] = 0;
}

struct Session {
    NvDRSSessionHandle h = nullptr;
    ~Session() { if (h) NvAPI_DRS_DestroySession(h); }
};

std::string fail(const char* what, NvAPI_Status s) { return std::string("err\t") + what + " " + std::to_string((int)s); }

// NVAPI_OK, or the error text in `why`.
bool open(Session& s, std::string& why) {
    NvAPI_Status st = NvAPI_Initialize();
    if (st != NVAPI_OK) { why = "err\tno_nvidia"; return false; }
    if ((st = NvAPI_DRS_CreateSession(&s.h)) != NVAPI_OK) { why = fail("session", st); return false; }
    if ((st = NvAPI_DRS_LoadSettings(s.h)) != NVAPI_OK) { why = fail("load", st); return false; }
    return true;
}

NvAPI_Status find(Session& s, const std::string& exe, NvDRSProfileHandle* profile) {
    NVDRS_APPLICATION app{};
    app.version = NVDRS_APPLICATION_VER;
    NvAPI_UnicodeString name;
    toUnicode(widen(exe), name);
    return NvAPI_DRS_FindApplicationByName(s.h, name, profile, &app);
}

// true and the value when this profile itself holds the setting (not inherited)
bool current(Session& s, NvDRSProfileHandle profile, NvU32& value) {
    NVDRS_SETTING setting{};
    setting.version = NVDRS_SETTING_VER;
    if (NvAPI_DRS_GetSetting(s.h, profile, PREFERRED_PSTATE_ID, &setting) != NVAPI_OK) return false;
    if (setting.settingLocation != NVDRS_CURRENT_PROFILE_LOCATION) return false;
    value = setting.u32CurrentValue;
    return true;
}

NvAPI_Status write(Session& s, NvDRSProfileHandle profile, NvU32 value) {
    NVDRS_SETTING setting{};
    setting.version = NVDRS_SETTING_VER;
    setting.settingId = PREFERRED_PSTATE_ID;
    setting.settingType = NVDRS_DWORD_TYPE;
    setting.u32CurrentValue = value;
    return NvAPI_DRS_SetSetting(s.h, profile, &setting);
}

}  // namespace

// "ok\tnone" or "ok\t<value>"
std::string nvGet(const std::string& exe) {
    Session s;
    std::string why;
    if (!open(s, why)) return why;
    NvDRSProfileHandle profile = nullptr;
    if (find(s, exe, &profile) != NVAPI_OK) return "ok\tnone";
    NvU32 value;
    return current(s, profile, value) ? "ok\t" + std::to_string(value) : "ok\tnone";
}

// "ok\t<created 0|1>;<old value or none>"
std::string nvSet(const std::string& exe) {
    Session s;
    std::string why;
    if (!open(s, why)) return why;
    NvDRSProfileHandle profile = nullptr;
    bool created = false;
    std::string old = "none";
    if (find(s, exe, &profile) == NVAPI_OK) {
        NvU32 value;
        if (current(s, profile, value)) old = std::to_string(value);
    } else {
        NVDRS_PROFILE info{};
        info.version = NVDRS_PROFILE_VER;
        toUnicode(widen(kPrefix + exe), info.profileName);
        NvAPI_Status st = NvAPI_DRS_CreateProfile(s.h, &info, &profile);
        if (st != NVAPI_OK) return fail("profile", st);
        NVDRS_APPLICATION app{};
        app.version = NVDRS_APPLICATION_VER;
        toUnicode(widen(exe), app.appName);
        if ((st = NvAPI_DRS_CreateApplication(s.h, profile, &app)) != NVAPI_OK) return fail("app", st);
        created = true;
    }
    NvAPI_Status st = write(s, profile, PREFERRED_PSTATE_PREFER_MAX);
    if (st != NVAPI_OK) return fail("set", st);
    if ((st = NvAPI_DRS_SaveSettings(s.h)) != NVAPI_OK) return fail("save", st);
    return "ok\t" + std::string(created ? "1" : "0") + ";" + old;
}

std::string nvRestore(const std::string& exe, bool created, const std::string& old) {
    Session s;
    std::string why;
    if (!open(s, why)) return why;
    NvDRSProfileHandle profile = nullptr;
    if (find(s, exe, &profile) != NVAPI_OK) return "ok\t";   // already gone
    NvAPI_Status st;
    if (created) {
        NVDRS_PROFILE info{};
        info.version = NVDRS_PROFILE_VER;
        if ((st = NvAPI_DRS_GetProfileInfo(s.h, profile, &info)) != NVAPI_OK) return fail("info", st);
        NvAPI_UnicodeString ours;
        toUnicode(widen(kPrefix + exe), ours);
        for (int i = 0; ours[i] || info.profileName[i]; i++)
            if (ours[i] != info.profileName[i]) return "err\tnot our profile";
        st = NvAPI_DRS_DeleteProfile(s.h, profile);
    } else if (old == "none") {
        st = NvAPI_DRS_DeleteProfileSetting(s.h, profile, PREFERRED_PSTATE_ID);
        if (st == NVAPI_SETTING_NOT_FOUND) st = NVAPI_OK;
    } else {
        st = write(s, profile, (NvU32)std::stoul(old));
    }
    if (st != NVAPI_OK) return fail("restore", st);
    if ((st = NvAPI_DRS_SaveSettings(s.h)) != NVAPI_OK) return fail("save", st);
    return "ok\t";
}
