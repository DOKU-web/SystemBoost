// Per-game settings through NVIDIA's public driver settings API: the "power management mode", and
// the frame rate limit that leaves frame generation room on a full GPU.
// A game the driver does not know gets its own "SystemBoost - x.exe" profile, which goes again with
// its last setting. In a profile that already exists only our setting changes, and the old value
// goes back on undo. Nothing here opens the game or its files.
#include "commands.h"
#include "nvapi.h"
#include "NvApiDriverSettings.h"
#include <mutex>
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
bool current(Session& s, NvDRSProfileHandle profile, NvU32 id, NvU32& value) {
    NVDRS_SETTING setting{};
    setting.version = NVDRS_SETTING_VER;
    if (NvAPI_DRS_GetSetting(s.h, profile, id, &setting) != NVAPI_OK) return false;
    if (setting.settingLocation != NVDRS_CURRENT_PROFILE_LOCATION) return false;
    value = setting.u32CurrentValue;
    return true;
}

NvAPI_Status write(Session& s, NvDRSProfileHandle profile, NvU32 id, NvU32 value) {
    NVDRS_SETTING setting{};
    setting.version = NVDRS_SETTING_VER;
    setting.settingId = id;
    setting.settingType = NVDRS_DWORD_TYPE;
    setting.u32CurrentValue = value;
    return NvAPI_DRS_SetSetting(s.h, profile, &setting);
}

// Empty when the profile is there, otherwise the error line.
std::string makeProfile(Session& s, const std::string& exe, NvDRSProfileHandle* profile) {
    NVDRS_PROFILE info{};
    info.version = NVDRS_PROFILE_VER;
    toUnicode(widen(kPrefix + exe), info.profileName);
    NvAPI_Status st = NvAPI_DRS_CreateProfile(s.h, &info, profile);
    if (st != NVAPI_OK) return fail("profile", st);
    NVDRS_APPLICATION app{};
    app.version = NVDRS_APPLICATION_VER;
    toUnicode(widen(exe), app.appName);
    if ((st = NvAPI_DRS_CreateApplication(s.h, *profile, &app)) != NVAPI_OK) return fail("app", st);
    return "";
}

// Two features can share our profile, so it goes with the last setting, not with the first undo.
NvAPI_Status dropIfEmpty(Session& s, NvDRSProfileHandle profile, const std::string& exe) {
    NVDRS_PROFILE info{};
    info.version = NVDRS_PROFILE_VER;
    NvAPI_Status st = NvAPI_DRS_GetProfileInfo(s.h, profile, &info);
    if (st != NVAPI_OK) return st;
    NvAPI_UnicodeString ours;
    toUnicode(widen(kPrefix + exe), ours);
    for (int i = 0; ours[i] || info.profileName[i]; i++)
        if (ours[i] != info.profileName[i]) return NVAPI_OK;   // the driver's own profile stays
    return info.numOfSettings == 0 ? NvAPI_DRS_DeleteProfile(s.h, profile) : NVAPI_OK;
}

std::string undo(const std::string& exe, NvU32 id, const std::string& old) {
    Session s;
    std::string why;
    if (!open(s, why)) return why;
    NvDRSProfileHandle profile = nullptr;
    if (find(s, exe, &profile) != NVAPI_OK) return "ok\t";   // already gone
    NvAPI_Status st;
    if (old == "none") {
        st = NvAPI_DRS_DeleteProfileSetting(s.h, profile, id);
        if (st == NVAPI_SETTING_NOT_FOUND) st = NVAPI_OK;
        if (st == NVAPI_OK) st = dropIfEmpty(s, profile, exe);
    } else {
        st = write(s, profile, id, (NvU32)std::stoul(old));
    }
    if (st != NVAPI_OK) return fail("restore", st);
    if ((st = NvAPI_DRS_SaveSettings(s.h)) != NVAPI_OK) return fail("save", st);
    return "ok\t";
}

// The limit in force. It only makes sense while frame generation runs, so it goes when the helper
// goes, whatever happened to the app.
std::mutex gLimitLock;
bool gLimited = false;
std::string gLimitExe, gLimitOld;

}  // namespace

// "ok\tnone" or "ok\t<value>"
std::string nvGet(const std::string& exe) {
    Session s;
    std::string why;
    if (!open(s, why)) return why;
    NvDRSProfileHandle profile = nullptr;
    if (find(s, exe, &profile) != NVAPI_OK) return "ok\tnone";
    NvU32 value;
    return current(s, profile, PREFERRED_PSTATE_ID, value) ? "ok\t" + std::to_string(value) : "ok\tnone";
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
        if (current(s, profile, PREFERRED_PSTATE_ID, value)) old = std::to_string(value);
    } else {
        why = makeProfile(s, exe, &profile);
        if (!why.empty()) return why;
        created = true;
    }
    NvAPI_Status st = write(s, profile, PREFERRED_PSTATE_ID, PREFERRED_PSTATE_PREFER_MAX);
    if (st != NVAPI_OK) return fail("set", st);
    if ((st = NvAPI_DRS_SaveSettings(s.h)) != NVAPI_OK) return fail("save", st);
    return "ok\t" + std::string(created ? "1" : "0") + ";" + old;
}

std::string nvRestore(const std::string& exe, const std::string& old) {
    return undo(exe, PREFERRED_PSTATE_ID, old);
}

// "ok\tset;<old value or none>", or "ok\tkeep;<value>" when the game already has a limit this low.
// A second, lower limit on top of ours reports the value from before the first.
std::string nvLimitSet(const std::string& exe, unsigned fps) {
    std::lock_guard<std::mutex> hold(gLimitLock);
    if (gLimited && gLimitExe != exe) {
        undo(gLimitExe, FRL_FPS_ID, gLimitOld);
        gLimited = false;
    }
    Session s;
    std::string why;
    if (!open(s, why)) return why;
    NvDRSProfileHandle profile = nullptr;
    std::string old = "none";
    if (find(s, exe, &profile) == NVAPI_OK) {
        NvU32 value;
        if (current(s, profile, FRL_FPS_ID, value)) {
            if (!gLimited && value != 0 && value <= fps) return "ok\tkeep;" + std::to_string(value);
            old = std::to_string(value);
        }
    } else {
        why = makeProfile(s, exe, &profile);
        if (!why.empty()) return why;
    }
    if (gLimited) old = gLimitOld;
    NvAPI_Status st = write(s, profile, FRL_FPS_ID, fps);
    if (st != NVAPI_OK) return fail("set", st);
    if ((st = NvAPI_DRS_SaveSettings(s.h)) != NVAPI_OK) return fail("save", st);
    gLimited = true;
    gLimitExe = exe;
    gLimitOld = old;
    return "ok\tset;" + old;
}

std::string nvLimitRestore(const std::string& exe, const std::string& old) {
    std::lock_guard<std::mutex> hold(gLimitLock);
    std::string reply = undo(exe, FRL_FPS_ID, old);
    if (gLimited && gLimitExe == exe && reply.rfind("ok", 0) == 0) gLimited = false;
    return reply;
}

void nvLimitShutdown() {
    std::lock_guard<std::mutex> hold(gLimitLock);
    if (gLimited) undo(gLimitExe, FRL_FPS_ID, gLimitOld);
    gLimited = false;
}
