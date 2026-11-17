// AMD Radeon through ADLX, AMD's own driver library (the calls are in amd_adlx.cpp):
//  - read-outs: amd.info, amd.metrics, amd.profile.get;
//  - the 3D settings a game session turns on and off (Anti-Lag, Chill). ADLX has no per-game
//    profiles, so they are global: the app applies them at game start and reverts them at game end;
//  - GPU tuning (power limit, GPU and VRAM max clock, ADLX auto tuning) that goes back by itself,
//    like gpuoc.cpp does for NVIDIA: the backup when the app does not confirm within 30 s, factory
//    values at once when the GPU runs too hot, its temperature cannot be read or the driver reports
//    a fault, and the backup when the helper exits.
// No Windows or ADLX headers: tests/amd_test.cpp runs this file with a fake driver.
#include "amd.h"
#include <mutex>
#include <utility>

namespace amd {
namespace {

std::mutex gLock;
Driver* gDriver = nullptr;
Hooks gHooks;

const Knob kKnobs[3] = {Knob::Power, Knob::GpuMax, Knob::VramMax};
const Toggle kProfileToggles[2] = {Toggle::AntiLag, Toggle::Chill};
const char* const kProfileNames[2] = {"antiLag", "chill"};

std::string ok(const std::string& data = "") { return "ok\t" + data; }
std::string err(const char* word) { return std::string("err\t") + word; }
std::string bit(bool b) { return b ? "1" : "0"; }

// "" when ADLX runs and `gpu` is in its list, otherwise the error reply.
std::string ready(int gpu) {
    if (!gDriver) return err("no_amd");
    switch (gDriver->open()) {
    case Status::Ok: break;
    case Status::OldDriver: return err("old_driver");
    case Status::Failed: return err("failed");
    default: return err("no_amd");
    }
    return gpu >= 0 && gpu < gDriver->gpuCount() ? "" : err("no_amd");
}

// ---------------------------------------------------------------- tuning
// The values from before the first apply ('-' = not supported) and whether all tuning was at factory.
struct Backup {
    bool factory = false;
    std::optional<int> v[3];
};

struct Tuning {
    bool active = false;
    bool confirmed = false;
    bool autoRunning = false;
    int gpu = 0;
    Backup backup;
    bool touched[3] = {false, false, false};   // what was written since the backup (auto tuning: all)
    unsigned long long confirmBy = 0;
    int tempLimit = 0;
    int hot = 0;
    int seconds = 0;
    std::string tripped = "0";
    std::string since;
};
Tuning gTune;
bool gWatching = false;

// "" or the error reply. Every supported value and the factory flag.
std::string readBackup(int gpu, Backup& b) {
    bool factory = false;
    b.factory = gDriver->atFactory(gpu, factory) == Res::Ok && factory;
    for (int k = 0; k < 3; k++) {
        Range r;
        Res res = gDriver->range(gpu, kKnobs[k], r);
        if (res == Res::Unsupported) { b.v[k].reset(); continue; }
        int value = 0;
        if (res != Res::Ok || gDriver->get(gpu, kKnobs[k], value) != Res::Ok) return err("failed");
        b.v[k] = value;
    }
    return "";
}

std::string backupText(const Backup& b) {
    std::string s;
    for (int k = 0; k < 3; k++) s += (k ? ";" : "") + (b.v[k] ? std::to_string(*b.v[k]) : std::string("-"));
    return s;
}

// Writes the given values and reads them back: false when a write failed or a value differs.
bool write(int gpu, const std::optional<int> (&want)[3]) {
    for (int k = 0; k < 3; k++)
        if (want[k] && gDriver->set(gpu, kKnobs[k], *want[k]) != Res::Ok) return false;
    for (int k = 0; k < 3; k++) {
        int got = 0;
        if (want[k] && (gDriver->get(gpu, kKnobs[k], got) != Res::Ok || got != *want[k])) return false;
    }
    return true;
}

// Puts the backup back. A card that was at factory gets ResetToFactory, which also undoes what auto
// tuning changed besides the three values; otherwise the values written since are written back.
void restore() {
    const Backup& b = gTune.backup;
    if (b.factory && gDriver->resetToFactory(gTune.gpu) == Res::Ok) return;
    for (int k = 0; k < 3; k++)
        if (b.v[k] && gTune.touched[k]) gDriver->set(gTune.gpu, kKnobs[k], *b.v[k]);
}

void stop(const char* tripped) {
    gTune.active = gTune.confirmed = gTune.autoRunning = false;
    gTune.touched[0] = gTune.touched[1] = gTune.touched[2] = false;
    gTune.hot = 0;
    gTune.tripped = tripped;
}

// A new backup unless a confirmed tuning is on: then the first backup stays.
void backUp(int gpu, const Backup& now) {
    if (gTune.active) return;
    gTune.backup = now;
    gTune.gpu = gpu;
    gTune.touched[0] = gTune.touched[1] = gTune.touched[2] = false;
}

// Too hot, no temperature or a driver fault: factory values at once (the owner's rule), the backup
// only when ADLX cannot reset.
void emergency(const char* why) {
    if (gDriver->resetToFactory(gTune.gpu) != Res::Ok) restore();
    stop(why);
}

void startWatch(int gpu, int tempLimit) {
    gTune.active = true;
    gTune.confirmed = false;
    gTune.gpu = gpu;
    gTune.confirmBy = gHooks.nowMs() + kConfirmMs;
    gTune.tempLimit = tempLimit;
    gTune.hot = 0;
    gTune.seconds = 0;
    gTune.tripped = "0";
    gTune.since = gHooks.utcNow ? gHooks.utcNow() : "";
    if (!gWatching) {
        gWatching = true;
        if (gHooks.startWatch) gHooks.startWatch();
    }
}

// "" when a new apply or auto tuning may start on `gpu`: nothing unconfirmed, and a temperature to watch.
std::string startable(int gpu) {
    if (gTune.autoRunning || (gTune.active && (!gTune.confirmed || gTune.gpu != gpu))) return err("busy");
    int t = 0;
    Res r = gDriver->temperature(gpu, t);
    if (r == Res::Unsupported) return err("unsupported");
    return r == Res::Ok ? "" : err("failed");
}

// "" when every given value is inside ADLX's range and on its step.
std::string inRange(int gpu, const std::optional<int> (&want)[3]) {
    for (int k = 0; k < 3; k++) {
        if (!want[k]) continue;
        Range r;
        Res res = gDriver->range(gpu, kKnobs[k], r);
        if (res == Res::Unsupported) return err("unsupported");
        if (res != Res::Ok) return err("failed");
        int v = *want[k];
        if (v < r.min || v > r.max || (r.step > 0 && (v - r.min) % r.step != 0)) return err("range");
    }
    return "";
}

// The tuned GPU was put back by the app: no watch, nothing tripped.
void settled(int gpu) {
    if (gTune.gpu != gpu) return;
    if (gTune.active) stop("0");
    gTune.tripped = "0";
}

// ---------------------------------------------------------------- 3D settings
// What this helper turned on or off, to undo when it exits. kLeave = not touched.
struct Profile {
    bool on = false;
    int gpu = 0;
    int old[2] = {kLeave, kLeave};
    int set[2] = {kLeave, kLeave};
};
Profile gProfile;

// The revert rule: current == old -> nothing; current == set -> old back; else the user changed it
// (`changed`, left alone). "" or the error reply.
std::string undoToggle(int gpu, int k, int old, int set, bool& changed) {
    changed = false;
    bool now = false;
    Res r = gDriver->toggleGet(gpu, kProfileToggles[k], now);
    if (r == Res::Unsupported) return "";   // gone with a driver change: nothing to put back
    if (r != Res::Ok) return err("failed");
    if ((now ? 1 : 0) == old) return "";
    if ((now ? 1 : 0) != set) { changed = true; return ""; }
    bool back = false;
    if (gDriver->toggleSet(gpu, kProfileToggles[k], old == 1) != Res::Ok ||
        gDriver->toggleGet(gpu, kProfileToggles[k], back) != Res::Ok || (back ? 1 : 0) != old)
        return err("failed");
    return "";
}

void undoProfile() {
    for (int k = 0; k < 2; k++) {
        bool changed = false;
        if (gProfile.old[k] != kLeave && gProfile.set[k] != kLeave)
            undoToggle(gProfile.gpu, k, gProfile.old[k], gProfile.set[k], changed);
    }
    gProfile = Profile();
}

std::string toggleText(int gpu, Toggle t) {
    bool on = false;
    return gDriver->toggleGet(gpu, t, on) == Res::Ok ? bit(on) : "-";
}

// ---------------------------------------------------------------- amd.info
const char* statusWord(Status s) {
    switch (s) {
    case Status::Ok: return "ok";
    case Status::NoDll: return "no_dll";
    case Status::OldDriver: return "old_driver";
    case Status::NoGpu: return "no_gpu";
    default: return "failed";
    }
}

std::string jsonString(const std::string& s) {
    static const char hex[] = "0123456789abcdef";
    std::string out = "\"";
    for (unsigned char c : s) {
        if (c == '"' || c == '\\') { out += '\\'; out += (char)c; }
        else if (c < 0x20 || c == 0x7f) { out += "\\u00"; out += hex[c >> 4]; out += hex[c & 15]; }
        else out += (char)c;
    }
    return out + "\"";
}

const char* yes(bool b) { return b ? "true" : "false"; }

// A section with "ok":false has no other fields.
std::string knobJson(int gpu, Knob k) {
    Range r;
    int now = 0;
    if (gDriver->range(gpu, k, r) != Res::Ok || gDriver->get(gpu, k, now) != Res::Ok) return "{\"ok\":false}";
    return "{\"ok\":true,\"min\":" + std::to_string(r.min) + ",\"max\":" + std::to_string(r.max) +
           ",\"step\":" + std::to_string(r.step) + ",\"now\":" + std::to_string(now) + "}";
}

std::string toggleJson(int gpu, Toggle t) {
    bool on = false;
    if (gDriver->toggleGet(gpu, t, on) != Res::Ok) return "{\"ok\":false}";
    return std::string("{\"ok\":true,\"on\":") + yes(on) + "}";
}

std::string gpuJson(int i) {
    bool factory = false;
    if (gDriver->atFactory(i, factory) != Res::Ok) factory = false;
    return "{\"i\":" + std::to_string(i) + ",\"name\":" + jsonString(gDriver->gpuName(i)) +
           ",\"integrated\":" + yes(gDriver->integrated(i)) + ",\"factory\":" + yes(factory) +
           ",\"power\":" + knobJson(i, Knob::Power) + ",\"gpuMax\":" + knobJson(i, Knob::GpuMax) +
           ",\"vramMax\":" + knobJson(i, Knob::VramMax) +
           ",\"auto\":{\"undervolt\":" + yes(gDriver->autoSupported(i, AutoMode::Undervolt)) +
           ",\"ocGpu\":" + yes(gDriver->autoSupported(i, AutoMode::OcGpu)) +
           ",\"ocVram\":" + yes(gDriver->autoSupported(i, AutoMode::OcVram)) + "}" +
           ",\"antiLag\":" + toggleJson(i, Toggle::AntiLag) + ",\"chill\":" + toggleJson(i, Toggle::Chill) +
           ",\"boost\":" + toggleJson(i, Toggle::Boost) + ",\"afmf\":" + toggleJson(i, Toggle::Afmf) + "}";
}

}  // namespace

void install(Driver* driver, Hooks hooks) {
    std::lock_guard<std::mutex> hold(gLock);
    gDriver = driver;
    gHooks = std::move(hooks);
    gTune = Tuning();
    gProfile = Profile();
    gWatching = false;
}

// ---------------------------------------------------------------- read-outs
std::string info() {
    std::lock_guard<std::mutex> hold(gLock);
    Status s = gDriver ? gDriver->open() : Status::Failed;
    int n = s == Status::Ok ? gDriver->gpuCount() : 0;
    if (s == Status::Ok && n <= 0) s = Status::NoGpu;
    std::string gpus;
    if (s == Status::Ok)
        for (int i = 0; i < n && i < 8; i++) gpus += (i ? "," : "") + gpuJson(i);
    return ok("{\"status\":\"" + std::string(statusWord(s)) + "\",\"adlx\":" + jsonString(gDriver ? gDriver->adlxVersion() : "") +
              ",\"driver\":" + jsonString(s == Status::Ok ? gDriver->driverVersion() : "") + ",\"gpus\":[" + gpus + "]}");
}

// "ok\t<tempC>;<hotspotC>;<gpuMHz>;<vramMHz>;<powerW>;<usagePct>;<vramUsedMB>", -1 = not supported
std::string metrics(int gpu) {
    std::lock_guard<std::mutex> hold(gLock);
    std::string why = ready(gpu);
    if (!why.empty()) return why;
    Metrics m;
    Res r = gDriver->metrics(gpu, m);
    if (r == Res::Unsupported) m = Metrics();
    else if (r != Res::Ok) return err("failed");
    const int v[7] = {m.tempC, m.hotspotC, m.gpuMHz, m.vramMHz, m.powerW, m.usagePct, m.vramUsedMB};
    std::string s;
    for (int i = 0; i < 7; i++) s += (i ? ";" : "") + std::to_string(v[i] < 0 ? -1 : v[i]);
    return ok(s);
}

// "ok\t<antiLag>;<chill>;<boost>;<afmf>", each 1, 0 or - (not supported)
std::string profileGet(int gpu) {
    std::lock_guard<std::mutex> hold(gLock);
    std::string why = ready(gpu);
    if (!why.empty()) return why;
    const Toggle all[4] = {Toggle::AntiLag, Toggle::Chill, Toggle::Boost, Toggle::Afmf};
    std::string s;
    for (int i = 0; i < 4; i++) {
        bool on = false;
        Res r = gDriver->toggleGet(gpu, all[i], on);
        if (r != Res::Ok && r != Res::Unsupported) return err("failed");
        s += (i ? ";" : "") + (r == Res::Ok ? bit(on) : std::string("-"));
    }
    return ok(s);
}

// ---------------------------------------------------------------- 3D settings
// Sets only the given ones. "ok\t<antiLag now>;<chill now>"
std::string profileApply(int gpu, int antiLag, int chill) {
    std::lock_guard<std::mutex> hold(gLock);
    std::string why = ready(gpu);
    if (!why.empty()) return why;
    const int want[2] = {antiLag, chill};
    bool before[2] = {false, false};
    for (int k = 0; k < 2; k++) {
        if (want[k] == kLeave) continue;
        Res r = gDriver->toggleGet(gpu, kProfileToggles[k], before[k]);
        if (r == Res::Unsupported) return err("unsupported");
        if (r != Res::Ok) return err("failed");
    }
    if (gProfile.on && gProfile.gpu != gpu && (want[0] != kLeave || want[1] != kLeave)) undoProfile();   // one GPU's at a time
    for (int k = 0; k < 2; k++) {
        if (want[k] == kLeave || before[k] == (want[k] == 1)) continue;
        bool now = false;
        if (gDriver->toggleSet(gpu, kProfileToggles[k], want[k] == 1) != Res::Ok ||
            gDriver->toggleGet(gpu, kProfileToggles[k], now) != Res::Ok || now != (want[k] == 1)) {
            for (int j = 0; j <= k; j++)
                if (want[j] != kLeave && before[j] != (want[j] == 1)) gDriver->toggleSet(gpu, kProfileToggles[j], before[j]);
            return err("failed");
        }
    }
    for (int k = 0; k < 2; k++) {
        if (want[k] == kLeave) continue;
        if (gProfile.old[k] == kLeave) gProfile.old[k] = before[k] ? 1 : 0;   // a second apply keeps the first old value
        gProfile.set[k] = want[k];
        gProfile.on = true;
        gProfile.gpu = gpu;
    }
    return ok(toggleText(gpu, Toggle::AntiLag) + ";" + toggleText(gpu, Toggle::Chill));
}

// "ok\t" or "ok\tchanged=antiLag,chill" (settings the user changed since, left alone)
std::string profileRevert(int gpu, int antiLagOld, int chillOld, int antiLagSet, int chillSet) {
    std::lock_guard<std::mutex> hold(gLock);
    std::string why = ready(gpu);
    if (!why.empty()) return why;
    const int old[2] = {antiLagOld, chillOld}, set[2] = {antiLagSet, chillSet};
    std::string changed;
    for (int k = 0; k < 2; k++) {
        if (old[k] == kLeave || set[k] == kLeave) continue;
        bool userChanged = false;
        if (!(why = undoToggle(gpu, k, old[k], set[k], userChanged)).empty()) return why;
        if (userChanged) changed += (changed.empty() ? "" : ",") + std::string(kProfileNames[k]);
        if (gProfile.on && gProfile.gpu == gpu) gProfile.old[k] = gProfile.set[k] = kLeave;
    }
    if (gProfile.on && gProfile.old[0] == kLeave && gProfile.old[1] == kLeave) gProfile = Profile();
    return ok(changed.empty() ? std::string() : "changed=" + changed);
}

// ---------------------------------------------------------------- tuning
// "ok\t<old power>;<old gpuMax>;<old vramMax>": the backup, '-' for a value the GPU does not have.
std::string tuningApply(int gpu, std::optional<int> powerPct, std::optional<int> gpuMaxMHz, std::optional<int> vramMaxMHz, int tempLimit) {
    std::lock_guard<std::mutex> hold(gLock);
    std::string why = ready(gpu);
    if (!why.empty()) return why;
    const std::optional<int> want[3] = {powerPct, gpuMaxMHz, vramMaxMHz};
    if (!want[0] && !want[1] && !want[2]) return err("refused");
    if (!(why = startable(gpu)).empty() || !(why = inRange(gpu, want)).empty()) return why;
    Backup now;
    if (!(why = readBackup(gpu, now)).empty()) return why;
    for (int k = 0; k < 3; k++) {
        if (!want[k]) continue;
        if (!now.v[k]) return err("failed");
        if (*want[k] > *now.v[k] + kMaxRaise[k]) return err("step");   // lowering is always allowed
    }
    backUp(gpu, now);
    for (int k = 0; k < 3; k++) gTune.touched[k] = gTune.touched[k] || want[k].has_value();
    if (!write(gpu, want)) {
        restore();
        stop("0");
        return err("failed");
    }
    startWatch(gpu, tempLimit);
    return ok(backupText(gTune.backup));
}

// ADLX auto tuning; the manual values are backed up first. Same reply and watch as amd.tuning.apply.
std::string autoApply(int gpu, AutoMode mode) {
    std::unique_lock<std::mutex> hold(gLock);
    std::string why = ready(gpu);
    if (!why.empty()) return why;
    if (!(why = startable(gpu)).empty()) return why;
    if (!gDriver->autoSupported(gpu, mode)) return err("unsupported");
    Backup now;
    if (!(why = readBackup(gpu, now)).empty()) return why;
    Res r = gDriver->autoStart(gpu, mode);
    if (r == Res::Unsupported) return err("unsupported");
    if (r != Res::Ok) return err("failed");   // ResetNeeded: the user's own one-click tuning stays as it is
    backUp(gpu, now);
    gTune.touched[0] = gTune.touched[1] = gTune.touched[2] = true;   // ADLX may change any of them
    startWatch(gpu, kAutoTempLimit);          // temperature and fault watch while ADLX tunes
    gTune.autoRunning = true;
    const unsigned long long until = gHooks.nowMs() + kAutoWaitMs;
    while (!gDriver->autoDone()) {
        if (gHooks.nowMs() >= until) {
            restore();
            stop("0");
            return err("failed");
        }
        hold.unlock();
        gHooks.sleepMs(100);
        hold.lock();
        if (!gTune.active || !gTune.autoRunning) return err("failed");   // the watch reset it, or the helper is exiting
    }
    gTune.autoRunning = false;
    gTune.confirmBy = gHooks.nowMs() + kConfirmMs;
    return ok(backupText(gTune.backup));
}

std::string tuningKeep() {
    std::lock_guard<std::mutex> hold(gLock);
    if (!gTune.active || gTune.autoRunning) return err("failed");   // already gone back (see amd.tuning.status)
    gTune.confirmed = true;
    return ok();
}

// "ok\t<active>;<confirmed>;<tempC or -1>;<tripped: 0, temp, fault, readfail or timeout>"
std::string tuningStatus() {
    std::lock_guard<std::mutex> hold(gLock);
    int t = -1;
    if (gDriver && gDriver->open() == Status::Ok && gTune.gpu < gDriver->gpuCount()) {
        int c = 0;
        if (gDriver->temperature(gTune.gpu, c) == Res::Ok) t = c;
    }
    return ok(bit(gTune.active) + ";" + bit(gTune.confirmed) + ";" + std::to_string(t) + ";" + gTune.tripped);
}

// The app's journal values (from amd.info before the apply): ADLX's range and step apply, no step limit.
std::string tuningRevert(int gpu, std::optional<int> powerPct, std::optional<int> gpuMaxMHz, std::optional<int> vramMaxMHz) {
    std::lock_guard<std::mutex> hold(gLock);
    std::string why = ready(gpu);
    if (!why.empty()) return why;
    const std::optional<int> want[3] = {powerPct, gpuMaxMHz, vramMaxMHz};
    if (!(why = inRange(gpu, want)).empty()) return why;
    if (!write(gpu, want)) return err("failed");   // the watch, if any, goes on
    settled(gpu);
    return ok();
}

std::string tuningReset(int gpu) {
    std::lock_guard<std::mutex> hold(gLock);
    std::string why = ready(gpu);
    if (!why.empty()) return why;
    Res r = gDriver->resetToFactory(gpu);
    if (r == Res::Unsupported) return err("unsupported");
    if (r != Res::Ok) return err("failed");
    settled(gpu);
    return ok();
}

void tick() {
    std::lock_guard<std::mutex> hold(gLock);
    if (!gDriver || !gTune.active) return;
    if (!gTune.confirmed && !gTune.autoRunning && gHooks.nowMs() > gTune.confirmBy) {
        restore();
        stop("timeout");
        return;
    }
    int t = 0;
    if (gDriver->temperature(gTune.gpu, t) != Res::Ok) { emergency("readfail"); return; }
    gTune.hot = t >= gTune.tempLimit ? gTune.hot + 1 : 0;
    if (gTune.hot >= kHotSamples) { emergency("temp"); return; }
    if (++gTune.seconds % kFaultCheckSeconds == 0 && gHooks.faultSince && gHooks.faultSince(gTune.since)) emergency("fault");
}

void shutdown() {
    std::lock_guard<std::mutex> hold(gLock);
    if (!gDriver) return;
    if (gTune.active) {
        restore();
        stop("0");
    }
    if (gProfile.on) undoProfile();
}

}  // namespace amd
