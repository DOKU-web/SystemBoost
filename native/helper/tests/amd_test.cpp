// The AMD rules and watch (src/amd.cpp) against a fake ADLX: no Windows, no AMD GPU, nothing real
// is written. Built and run by framegen/tests/run_all.sh.
#include "../src/amd.h"
#include <cstdio>
#include <string>
#include <vector>

static int failed = 0, checks = 0;
#define CHECK(x) do { checks++; if (!(x)) { printf("FAIL line %d: %s\n", __LINE__, #x); failed++; } } while (0)
#define EQ(a, b) do { checks++; std::string a_ = (a), b_ = (b); if (a_ != b_) { \
    printf("FAIL line %d: %s\n  got  [%s]\n  want [%s]\n", __LINE__, #a, a_.c_str(), b_.c_str()); failed++; } } while (0)

using amd::Res;

struct Fake : amd::Driver {
    amd::Status status = amd::Status::Ok;
    int gpus = 1;
    std::string name = "AMD Radeon RX 7800 XT";
    bool integratedGpu = false;
    bool tuningOk = true;                                      // GPUTuningServices at all
    bool knobOk[3] = {true, true, true};
    amd::Range ranges[3] = {{-10, 15, 1}, {500, 2900, 1}, {2400, 2700, 1}};
    int factoryValue[3] = {0, 2430, 2425};
    int value[3] = {0, 2430, 2425};
    bool voltageTuned = false;                                 // what only a factory reset undoes
    int failSet = -1;                                          // this knob's write fails
    int setOffset = 0;                                         // a write lands this far off (read-back check)
    int sets = 0, resets = 0;
    bool resetFails = false;

    bool autoOk[3] = {true, true, true};
    Res autoResult = Res::Ok;
    int autoPollsUntilDone = 2;                                // -1: ADLX never calls back
    int autoPolls = 0, autoStarts = 0;

    bool toggleOk[4] = {true, true, true, true};
    bool toggle[4] = {false, false, false, false};
    int failToggleSet = -1;
    int toggleSets = 0;

    bool perfOk = true, metricsFail = false;
    amd::Metrics m;
    std::vector<int> temps;                                    // one per read, then `temp`
    int temp = 60;
    bool tempUnsupported = false, tempFails = false;

    amd::Status open() override { return status; }
    std::string adlxVersion() override { return status == amd::Status::NoDll ? "" : "2.0"; }
    std::string driverVersion() override { return "24.10.1"; }
    int gpuCount() override { return status == amd::Status::Ok ? gpus : 0; }
    std::string gpuName(int) override { return name; }
    bool integrated(int) override { return integratedGpu; }

    Res atFactory(int, bool& f) override {
        if (!tuningOk) return Res::Unsupported;
        f = !voltageTuned && value[0] == factoryValue[0] && value[1] == factoryValue[1] && value[2] == factoryValue[2];
        return Res::Ok;
    }
    Res resetToFactory(int) override {
        if (!tuningOk) return Res::Unsupported;
        resets++;
        if (resetFails) return Res::Failed;
        for (int k = 0; k < 3; k++) value[k] = factoryValue[k];
        voltageTuned = false;
        return Res::Ok;
    }
    Res range(int, amd::Knob k, amd::Range& r) override {
        if (!tuningOk || !knobOk[(int)k]) return Res::Unsupported;
        r = ranges[(int)k];
        return Res::Ok;
    }
    Res get(int, amd::Knob k, int& v) override {
        if (!tuningOk || !knobOk[(int)k]) return Res::Unsupported;
        v = value[(int)k];
        return Res::Ok;
    }
    Res set(int, amd::Knob k, int v) override {
        if (!tuningOk || !knobOk[(int)k]) return Res::Unsupported;
        if ((int)k == failSet) return Res::Failed;
        sets++;
        value[(int)k] = v + setOffset;
        return Res::Ok;
    }
    bool autoSupported(int, amd::AutoMode mode) override { return tuningOk && autoOk[(int)mode]; }
    Res autoStart(int, amd::AutoMode mode) override {
        if (!autoSupported(0, mode)) return Res::Unsupported;
        if (autoResult != Res::Ok) return autoResult;
        autoStarts++;
        autoPolls = 0;
        return Res::Ok;
    }
    bool autoDone() override {
        if (autoPollsUntilDone < 0 || ++autoPolls < autoPollsUntilDone) return false;
        voltageTuned = true;   // what an undervolt does
        value[1] = 2500;
        return true;
    }
    Res toggleGet(int, amd::Toggle t, bool& on) override {
        if (!toggleOk[(int)t]) return Res::Unsupported;
        on = toggle[(int)t];
        return Res::Ok;
    }
    Res toggleSet(int, amd::Toggle t, bool on) override {
        if (!toggleOk[(int)t]) return Res::Unsupported;
        if ((int)t == failToggleSet) return Res::Failed;
        toggleSets++;
        toggle[(int)t] = on;
        return Res::Ok;
    }
    Res metrics(int, amd::Metrics& out) override {
        if (!perfOk) return Res::Unsupported;
        if (metricsFail) return Res::Failed;
        out = m;
        return Res::Ok;
    }
    Res temperature(int, int& c) override {
        if (tempUnsupported) return Res::Unsupported;
        if (tempFails) return Res::Failed;
        if (!temps.empty()) { c = temps.front(); temps.erase(temps.begin()); }
        else c = temp;
        return Res::Ok;
    }

    std::string values() const { return std::to_string(value[0]) + ";" + std::to_string(value[1]) + ";" + std::to_string(value[2]); }
};

// fake platform
static unsigned long long gNow = 1000000;
static bool gFault = false;
static int gWatchStarts = 0;
static bool gTickWhileSleeping = false;   // the watch thread runs while autoApply waits

static void setup(Fake& f) {
    gNow = 1000000;
    gFault = false;
    gWatchStarts = 0;
    gTickWhileSleeping = false;
    amd::Hooks h;
    h.nowMs = [] { return gNow; };
    h.sleepMs = [](unsigned ms) {
        unsigned long long before = gNow;
        gNow += ms;
        if (gTickWhileSleeping && gNow / 1000 != before / 1000) amd::tick();
    };
    h.utcNow = [] { return std::string("2026-11-17T12:00:00Z"); };
    h.faultSince = [](const std::string& utc) { return gFault && utc == "2026-11-17T12:00:00Z"; };
    h.startWatch = [] { gWatchStarts++; };
    amd::install(&f, h);
}

// n watch samples, one second apart
static void seconds(int n) {
    for (int i = 0; i < n; i++) {
        gNow += 1000;
        amd::tick();
    }
}

static const std::optional<int> none;

static void testNoAmd() {
    Fake f;
    f.status = amd::Status::NoDll;
    setup(f);
    EQ(amd::info(), "ok\t{\"status\":\"no_dll\",\"adlx\":\"\",\"driver\":\"\",\"gpus\":[]}");
    EQ(amd::metrics(0), "err\tno_amd");
    EQ(amd::profileGet(0), "err\tno_amd");
    EQ(amd::profileApply(0, 1, amd::kLeave), "err\tno_amd");
    EQ(amd::tuningApply(0, 5, none, none, 83), "err\tno_amd");
    EQ(amd::autoApply(0, amd::AutoMode::Undervolt), "err\tno_amd");
    EQ(amd::tuningReset(0), "err\tno_amd");
    EQ(amd::tuningStatus(), "ok\t0;0;-1;0");
    EQ(amd::tuningKeep(), "err\tfailed");
    amd::shutdown();
    CHECK(f.sets == 0 && f.resets == 0 && f.toggleSets == 0);

    // a helper built without ADLX (SB_ADLX=OFF) says "off", never "update your driver"
    Fake o;
    o.status = amd::Status::Off;
    setup(o);
    EQ(amd::info(), "ok\t{\"status\":\"off\",\"adlx\":\"2.0\",\"driver\":\"\",\"gpus\":[]}");
    EQ(amd::metrics(0), "err\toff");
    EQ(amd::profileGet(0), "err\toff");
    EQ(amd::profileApply(0, 1, amd::kLeave), "err\toff");
    EQ(amd::tuningApply(0, 5, none, none, 83), "err\toff");
    EQ(amd::autoApply(0, amd::AutoMode::Undervolt), "err\toff");
    EQ(amd::tuningReset(0), "err\toff");
    EQ(amd::tuningStatus(), "ok\t0;0;-1;0");
    amd::shutdown();
    CHECK(o.sets == 0 && o.resets == 0 && o.toggleSets == 0);

    Fake g;
    g.status = amd::Status::NoGpu;
    setup(g);
    EQ(amd::info(), "ok\t{\"status\":\"no_gpu\",\"adlx\":\"2.0\",\"driver\":\"\",\"gpus\":[]}");
    EQ(amd::metrics(0), "err\tno_amd");

    Fake h;   // ADLX runs but lists no GPU
    h.gpus = 0;
    setup(h);
    EQ(amd::info(), "ok\t{\"status\":\"no_gpu\",\"adlx\":\"2.0\",\"driver\":\"\",\"gpus\":[]}");

    Fake one;   // an index past ADLX's list
    setup(one);
    EQ(amd::metrics(1), "err\tno_amd");
    EQ(amd::tuningApply(7, 5, none, none, 83), "err\tno_amd");

    Fake broken;
    broken.status = amd::Status::Failed;
    setup(broken);
    EQ(amd::info(), "ok\t{\"status\":\"failed\",\"adlx\":\"2.0\",\"driver\":\"\",\"gpus\":[]}");
    EQ(amd::profileGet(0), "err\tfailed");

    amd::install(nullptr, amd::Hooks());   // no driver at all
    EQ(amd::info(), "ok\t{\"status\":\"failed\",\"adlx\":\"\",\"driver\":\"\",\"gpus\":[]}");
    EQ(amd::metrics(0), "err\tno_amd");
    amd::tick();
    amd::shutdown();
}

static void testOldDriver() {
    Fake f;
    f.status = amd::Status::OldDriver;
    setup(f);
    EQ(amd::info(), "ok\t{\"status\":\"old_driver\",\"adlx\":\"2.0\",\"driver\":\"\",\"gpus\":[]}");
    EQ(amd::metrics(0), "err\told_driver");
    EQ(amd::profileApply(0, 1, 1), "err\told_driver");
    EQ(amd::tuningApply(0, 5, none, none, 83), "err\told_driver");
    EQ(amd::tuningRevert(0, 0, none, none), "err\told_driver");
    CHECK(f.sets == 0 && f.toggleSets == 0);
}

static void testInfo() {
    Fake f;
    f.name = "AMD \"Radeon\"\tX";
    f.toggle[1] = true;
    f.toggleOk[3] = false;
    setup(f);
    EQ(amd::info(),
       "ok\t{\"status\":\"ok\",\"adlx\":\"2.0\",\"driver\":\"24.10.1\",\"gpus\":[{\"i\":0,\"name\":\"AMD \\\"Radeon\\\"\\u0009X\","
       "\"integrated\":false,\"factory\":true,"
       "\"power\":{\"ok\":true,\"min\":-10,\"max\":15,\"step\":1,\"now\":0},"
       "\"gpuMax\":{\"ok\":true,\"min\":500,\"max\":2900,\"step\":1,\"now\":2430},"
       "\"vramMax\":{\"ok\":true,\"min\":2400,\"max\":2700,\"step\":1,\"now\":2425},"
       "\"auto\":{\"undervolt\":true,\"ocGpu\":true,\"ocVram\":true},"
       "\"antiLag\":{\"ok\":true,\"on\":false},\"chill\":{\"ok\":true,\"on\":true},"
       "\"boost\":{\"ok\":true,\"on\":false},\"afmf\":{\"ok\":false}}]}");

    Fake old;   // a pre-RDNA card: only state-based clock tuning, an APU without auto tuning
    old.knobOk[1] = old.knobOk[2] = false;
    old.autoOk[0] = old.autoOk[1] = old.autoOk[2] = false;
    old.integratedGpu = true;
    old.gpus = 2;
    setup(old);
    std::string j = amd::info();
    CHECK(j.find("\"gpuMax\":{\"ok\":false},\"vramMax\":{\"ok\":false}") != std::string::npos);
    CHECK(j.find("\"auto\":{\"undervolt\":false,\"ocGpu\":false,\"ocVram\":false}") != std::string::npos);
    CHECK(j.find("\"integrated\":true") != std::string::npos);
    CHECK(j.find("{\"i\":1,") != std::string::npos);
    CHECK(j.find("\"power\":{\"ok\":true") != std::string::npos);
    CHECK(old.sets == 0 && old.resets == 0);

    Fake no;   // no tuning services at all
    no.tuningOk = false;
    setup(no);
    j = amd::info();
    CHECK(j.find("\"factory\":false,\"power\":{\"ok\":false},\"gpuMax\":{\"ok\":false},\"vramMax\":{\"ok\":false}") != std::string::npos);
}

static void testReadouts() {
    Fake f;
    f.m.tempC = 61;
    f.m.hotspotC = 75;
    f.m.gpuMHz = 2400;
    f.m.vramMHz = 2425;
    f.m.powerW = 210;
    f.m.usagePct = 97;
    f.m.vramUsedMB = 8123;
    setup(f);
    EQ(amd::metrics(0), "ok\t61;75;2400;2425;210;97;8123");
    f.m.hotspotC = -1;
    f.m.vramUsedMB = -1;
    EQ(amd::metrics(0), "ok\t61;-1;2400;2425;210;97;-1");
    f.perfOk = false;
    EQ(amd::metrics(0), "ok\t-1;-1;-1;-1;-1;-1;-1");
    f.perfOk = true;
    f.metricsFail = true;
    EQ(amd::metrics(0), "err\tfailed");

    f.toggle[0] = true;
    f.toggleOk[2] = false;
    EQ(amd::profileGet(0), "ok\t1;0;-;0");
}

static void testUnsupported() {
    Fake f;
    f.knobOk[1] = false;   // pre-RDNA: no single GPU max clock
    f.toggleOk[1] = false;
    f.autoOk[2] = false;
    setup(f);
    EQ(amd::tuningApply(0, none, 2500, none, 83), "err\tunsupported");
    EQ(amd::profileApply(0, 1, 1), "err\tunsupported");
    CHECK(!f.toggle[0]);   // nothing written when one of them is unsupported
    EQ(amd::autoApply(0, amd::AutoMode::OcVram), "err\tunsupported");
    EQ(amd::tuningRevert(0, none, 2430, none), "err\tunsupported");
    EQ(amd::tuningApply(0, 5, none, none, 83), "ok\t0;-;2425");   // what it has still works

    Fake t;   // no temperature: nothing could watch the tuning
    t.tempUnsupported = true;
    setup(t);
    EQ(amd::tuningApply(0, 5, none, none, 83), "err\tunsupported");
    EQ(amd::autoApply(0, amd::AutoMode::Undervolt), "err\tunsupported");
    t.tempUnsupported = false;
    t.tempFails = true;
    EQ(amd::tuningApply(0, 5, none, none, 83), "err\tfailed");
    CHECK(t.sets == 0 && t.autoStarts == 0);

    Fake r;
    r.tuningOk = false;
    setup(r);
    EQ(amd::tuningReset(0), "err\tunsupported");
    EQ(amd::tuningApply(0, 5, none, none, 83), "err\tunsupported");
}

static void testRangeAndStep() {
    Fake f;
    f.ranges[1] = {500, 2900, 5};
    setup(f);
    EQ(amd::tuningApply(0, 16, none, none, 83), "err\trange");
    EQ(amd::tuningApply(0, -11, none, none, 83), "err\trange");
    EQ(amd::tuningApply(0, none, 2433, none, 83), "err\trange");   // off the 5 MHz step
    EQ(amd::tuningApply(0, none, 495, none, 83), "err\trange");
    EQ(amd::tuningApply(0, none, none, 2701, 83), "err\trange");
    EQ(amd::tuningApply(0, 6, none, none, 83), "err\tstep");       // more than +5 % at once
    EQ(amd::tuningApply(0, none, 2535, none, 83), "err\tstep");    // more than +100 MHz
    EQ(amd::tuningApply(0, none, none, 2476, 83), "err\tstep");    // more than +50 MHz
    EQ(amd::tuningApply(0, 5, 2535, 2475, 83), "err\tstep");       // power and VRAM ok, GPU max too far
    CHECK(f.sets == 0);
    EQ(amd::tuningApply(0, none, none, none, 83), "err\trefused");
    EQ(amd::tuningApply(0, 5, 2530, 2475, 83), "ok\t0;2430;2425");   // exactly the largest raises
    EQ(f.values(), "5;2530;2475");
    EQ(amd::tuningKeep(), "ok\t");
    EQ(amd::tuningApply(0, 10, 2630, 2525, 83), "ok\t0;2430;2425");   // the next small step from there; first backup stays
    EQ(amd::tuningKeep(), "ok\t");

    Fake low;   // lowering is always allowed, however far
    setup(low);
    EQ(amd::tuningApply(0, -10, 500, 2400, 83), "ok\t0;2430;2425");
    EQ(low.values(), "-10;500;2400");
}

static void testApplyFailure() {
    Fake f;   // the user's own tuning before: the three values are written back
    f.value[0] = 3;
    f.failSet = 1;
    setup(f);
    EQ(amd::tuningApply(0, 5, 2500, none, 83), "err\tfailed");
    EQ(f.values(), "3;2430;2425");
    CHECK(f.resets == 0);
    EQ(amd::tuningStatus(), "ok\t0;0;60;0");

    Fake z;   // at factory before: put back with ResetToFactory
    z.failSet = 2;
    setup(z);
    EQ(amd::tuningApply(0, 5, none, 2450, 83), "err\tfailed");
    CHECK(z.resets == 1);
    EQ(z.values(), "0;2430;2425");

    Fake rb;   // the card reads back something else
    rb.value[0] = 2;
    rb.setOffset = 1;
    setup(rb);
    EQ(amd::tuningApply(0, 4, none, none, 83), "err\tfailed");
    EQ(rb.values(), "3;2430;2425");   // the backup was written (and lands off too, the fake's rule)
    rb.setOffset = 0;
    EQ(amd::tuningApply(0, 4, none, none, 83), "ok\t3;2430;2425");   // not busy: nothing is active
}

static void testBusyKeepTimeout() {
    Fake f;
    f.value[0] = 2;
    setup(f);
    EQ(amd::tuningApply(0, 5, none, none, 83), "ok\t2;2430;2425");
    CHECK(gWatchStarts == 1);
    EQ(amd::tuningApply(0, 4, none, none, 83), "err\tbusy");
    EQ(amd::autoApply(0, amd::AutoMode::Undervolt), "err\tbusy");
    EQ(amd::tuningStatus(), "ok\t1;0;60;0");
    seconds(29);
    EQ(amd::tuningStatus(), "ok\t1;0;60;0");
    EQ(amd::tuningKeep(), "ok\t");
    EQ(amd::tuningStatus(), "ok\t1;1;60;0");
    seconds(120);
    EQ(amd::tuningStatus(), "ok\t1;1;60;0");
    EQ(f.values(), "5;2430;2425");

    Fake g;   // not confirmed in time: the backup goes back
    g.value[0] = 2;
    setup(g);
    EQ(amd::tuningApply(0, 7, none, none, 83), "ok\t2;2430;2425");
    seconds(30);
    EQ(amd::tuningStatus(), "ok\t1;0;60;0");
    seconds(1);
    EQ(amd::tuningStatus(), "ok\t0;0;60;timeout");
    EQ(g.values(), "2;2430;2425");
    EQ(amd::tuningKeep(), "err\tfailed");   // too late
    EQ(amd::tuningApply(0, 3, none, none, 83), "ok\t2;2430;2425");   // a fresh start
    EQ(amd::tuningStatus(), "ok\t1;0;60;0");

    Fake two;   // one tuned GPU at a time
    two.gpus = 2;
    setup(two);
    EQ(amd::tuningApply(0, 5, none, none, 83), "ok\t0;2430;2425");
    EQ(amd::tuningKeep(), "ok\t");
    EQ(amd::tuningApply(1, 5, none, none, 83), "err\tbusy");
}

static void testWatch() {
    Fake f;
    setup(f);
    EQ(amd::tuningApply(0, 5, none, none, 83), "ok\t0;2430;2425");
    EQ(amd::tuningKeep(), "ok\t");
    f.temps = {84, 85, 70, 83, 84, 60};   // a dip starts the count again
    seconds(6);
    EQ(amd::tuningStatus(), "ok\t1;1;60;0");
    f.temps = {83, 84};
    seconds(2);
    EQ(amd::tuningStatus(), "ok\t1;1;60;0");
    CHECK(f.resets == 0);
    f.temp = 90;
    seconds(1);   // the third sample at or over the limit
    EQ(amd::tuningStatus(), "ok\t0;0;90;temp");
    CHECK(f.resets == 1);
    EQ(f.values(), "0;2430;2425");

    Fake user;   // the emergency is factory values even when the user had their own tuning
    user.value[1] = 2400;
    setup(user);
    EQ(amd::tuningApply(0, 5, none, none, 70), "ok\t0;2400;2425");
    user.temp = 70;
    seconds(3);
    EQ(amd::tuningStatus(), "ok\t0;0;70;temp");
    EQ(user.values(), "0;2430;2425");

    Fake rf;   // temperature read fails
    setup(rf);
    EQ(amd::tuningApply(0, 5, none, none, 83), "ok\t0;2430;2425");
    rf.tempFails = true;
    seconds(1);
    rf.tempFails = false;
    EQ(amd::tuningStatus(), "ok\t0;0;60;readfail");
    CHECK(rf.resets == 1);

    Fake df;   // a driver fault, checked every 2 s
    setup(df);
    EQ(amd::tuningApply(0, 5, none, none, 83), "ok\t0;2430;2425");
    EQ(amd::tuningKeep(), "ok\t");
    gFault = true;
    seconds(1);
    EQ(amd::tuningStatus(), "ok\t1;1;60;0");
    seconds(1);
    EQ(amd::tuningStatus(), "ok\t0;0;60;fault");
    CHECK(df.resets == 1);
    EQ(df.values(), "0;2430;2425");

    Fake nr;   // ADLX cannot reset: the backup instead
    nr.value[0] = 2;
    nr.resetFails = true;
    setup(nr);
    EQ(amd::tuningApply(0, 5, none, none, 83), "ok\t2;2430;2425");
    nr.tempFails = true;
    seconds(1);
    EQ(nr.values(), "2;2430;2425");

    Fake idle;   // nothing active: the watch reads nothing
    setup(idle);
    idle.tempFails = true;
    gFault = true;
    seconds(5);
    CHECK(idle.resets == 0 && idle.sets == 0);
}

static void testExit() {
    Fake f;
    f.value[0] = 1;
    setup(f);
    EQ(amd::profileApply(0, 1, 1), "ok\t1;1");
    EQ(amd::tuningApply(0, 6, 2500, none, 83), "ok\t1;2430;2425");
    EQ(amd::tuningKeep(), "ok\t");   // even a confirmed tuning goes when the helper goes
    amd::shutdown();
    EQ(f.values(), "1;2430;2425");
    CHECK(!f.toggle[0] && !f.toggle[1]);
    EQ(amd::tuningStatus(), "ok\t0;0;60;0");
    int sets = f.sets, toggles = f.toggleSets;
    amd::shutdown();   // twice: nothing more
    CHECK(f.sets == sets && f.toggleSets == toggles);

    Fake z;   // at factory before: ResetToFactory
    setup(z);
    EQ(amd::tuningApply(0, 5, none, none, 83), "ok\t0;2430;2425");
    amd::shutdown();
    CHECK(z.resets == 1);
    EQ(z.values(), "0;2430;2425");
}

static void testProfile() {
    Fake f;
    setup(f);
    EQ(amd::profileApply(0, 1, amd::kLeave), "ok\t1;0");
    CHECK(f.toggle[0] && !f.toggle[1]);
    EQ(amd::profileRevert(0, 0, amd::kLeave, 1, amd::kLeave), "ok\t");   // current == set: old back
    CHECK(!f.toggle[0]);
    int writes = f.toggleSets;
    EQ(amd::profileRevert(0, 0, amd::kLeave, 1, amd::kLeave), "ok\t");   // current == old: nothing
    CHECK(f.toggleSets == writes);
    amd::shutdown();   // the helper forgot it after the revert
    CHECK(f.toggleSets == writes);

    Fake u;   // the user changed it since: left alone and reported
    u.toggle[0] = true;
    setup(u);
    EQ(amd::profileApply(0, 1, 0), "ok\t1;0");   // both already so
    CHECK(u.toggleSets == 0);
    u.toggle[0] = false;
    u.toggle[1] = true;
    EQ(amd::profileRevert(0, 1, 0, 1, 0), "ok\tchanged=antiLag,chill");
    CHECK(!u.toggle[0] && u.toggle[1] && u.toggleSets == 0);
    amd::shutdown();
    CHECK(u.toggleSets == 0);

    Fake x;   // reverted at helper exit with the same rule
    setup(x);
    EQ(amd::profileApply(0, 1, 1), "ok\t1;1");
    EQ(amd::profileApply(0, 0, amd::kLeave), "ok\t0;1");   // a second apply keeps the first old value
    amd::shutdown();
    CHECK(!x.toggle[0] && !x.toggle[1]);

    Fake c;   // at exit, a setting the user changed stays
    setup(c);
    EQ(amd::profileApply(0, 1, 1), "ok\t1;1");
    c.toggle[1] = false;   // user turned Chill off: current == old, nothing to do
    c.toggle[0] = true;
    amd::shutdown();
    CHECK(!c.toggle[0] && !c.toggle[1]);

    Fake bad;   // a write fails: the earlier one goes back
    bad.failToggleSet = 1;
    setup(bad);
    EQ(amd::profileApply(0, 1, 1), "err\tfailed");
    CHECK(!bad.toggle[0] && !bad.toggle[1]);
    amd::shutdown();
    CHECK(!bad.toggle[0]);

    Fake g2;   // another GPU's settings: the first GPU's go back first
    g2.gpus = 2;
    setup(g2);
    EQ(amd::profileApply(0, 1, amd::kLeave), "ok\t1;0");
    EQ(amd::profileApply(1, amd::kLeave, amd::kLeave), "ok\t1;0");   // nothing asked on GPU 1 (fake shares state)
    CHECK(g2.toggle[0]);
    EQ(amd::profileApply(1, amd::kLeave, 1), "ok\t0;1");             // GPU 0's Anti-Lag went back first
}

static void testAuto() {
    Fake f;   // the user's own values before: written back on timeout
    f.value[0] = 2;
    setup(f);
    EQ(amd::autoApply(0, amd::AutoMode::Undervolt), "ok\t2;2430;2425");
    CHECK(f.autoStarts == 1 && f.voltageTuned);
    EQ(f.values(), "2;2500;2425");
    EQ(amd::tuningStatus(), "ok\t1;0;60;0");
    EQ(amd::tuningApply(0, 3, none, none, 83), "err\tbusy");
    seconds(31);
    EQ(amd::tuningStatus(), "ok\t0;0;60;timeout");
    EQ(f.values(), "2;2430;2425");

    Fake z;   // at factory before: ResetToFactory also undoes the undervolt
    setup(z);
    EQ(amd::autoApply(0, amd::AutoMode::Undervolt), "ok\t0;2430;2425");
    EQ(amd::tuningKeep(), "ok\t");
    EQ(amd::tuningRevert(0, 0, 2430, 2425), "ok\t");   // the app's journal
    EQ(z.values(), "0;2430;2425");
    EQ(amd::tuningStatus(), "ok\t0;0;60;0");
    CHECK(z.voltageTuned);   // the three values alone do not undo an undervolt: amd.tuning.reset does
    EQ(amd::tuningReset(0), "ok\t");
    CHECK(!z.voltageTuned);
    EQ(amd::autoApply(0, amd::AutoMode::OcGpu), "ok\t0;2430;2425");
    CHECK(z.voltageTuned);
    amd::shutdown();   // the helper's own backup knew the card was at factory
    CHECK(!z.voltageTuned && z.resets == 2);

    Fake rn;   // the user's own one-click tuning is on: left alone
    rn.autoResult = Res::ResetNeeded;
    setup(rn);
    EQ(amd::autoApply(0, amd::AutoMode::OcGpu), "err\tfailed");
    CHECK(rn.resets == 0 && rn.sets == 0);
    EQ(amd::tuningStatus(), "ok\t0;0;60;0");

    Fake never;   // no callback within 60 s: the backup goes back
    never.value[0] = 1;
    never.autoPollsUntilDone = -1;
    setup(never);
    unsigned long long start = gNow;
    EQ(amd::autoApply(0, amd::AutoMode::OcVram), "err\tfailed");
    CHECK(gNow - start >= amd::kAutoWaitMs && gNow - start < amd::kAutoWaitMs + 1000);
    EQ(never.values(), "1;2430;2425");
    EQ(amd::tuningStatus(), "ok\t0;0;60;0");

    Fake hot;   // too hot while ADLX tunes: the watch resets at once and the apply fails
    hot.autoPollsUntilDone = 40;
    setup(hot);
    gTickWhileSleeping = true;
    hot.temp = 86;
    EQ(amd::autoApply(0, amd::AutoMode::OcGpu), "err\tfailed");
    gTickWhileSleeping = false;
    EQ(amd::tuningStatus(), "ok\t0;0;86;temp");
    CHECK(hot.resets == 1);

    Fake slow;   // the confirm time starts when ADLX is done, not when it started
    slow.autoPollsUntilDone = 450;   // about 45 s
    setup(slow);
    gTickWhileSleeping = true;
    EQ(amd::autoApply(0, amd::AutoMode::Undervolt), "ok\t0;2430;2425");
    gTickWhileSleeping = false;
    seconds(29);
    EQ(amd::tuningStatus(), "ok\t1;0;60;0");
    EQ(amd::tuningKeep(), "ok\t");
}

static void testRevertReset() {
    Fake f;
    f.value[0] = 2;
    setup(f);
    EQ(amd::tuningApply(0, 7, none, none, 83), "ok\t2;2430;2425");
    EQ(amd::tuningRevert(0, 16, none, none), "err\trange");
    EQ(amd::tuningStatus(), "ok\t1;0;60;0");   // the watch goes on
    EQ(amd::tuningRevert(0, 2, none, none), "ok\t");
    EQ(amd::tuningStatus(), "ok\t0;0;60;0");
    seconds(40);
    EQ(f.values(), "2;2430;2425");

    Fake big;   // going back has no step limit
    big.value[0] = -10;
    big.value[1] = 500;
    setup(big);
    EQ(amd::tuningRevert(0, 15, 2900, 2700), "ok\t");
    EQ(big.values(), "15;2900;2700");
    big.failSet = 0;
    EQ(amd::tuningRevert(0, 0, none, none), "err\tfailed");
    big.failSet = -1;
    big.setOffset = 1;
    EQ(amd::tuningRevert(0, 0, none, none), "err\tfailed");

    Fake r;
    setup(r);
    EQ(amd::tuningApply(0, 5, 2500, none, 83), "ok\t0;2430;2425");
    r.resetFails = true;
    EQ(amd::tuningReset(0), "err\tfailed");
    EQ(amd::tuningStatus(), "ok\t1;0;60;0");
    r.resetFails = false;
    EQ(amd::tuningReset(0), "ok\t");
    EQ(r.values(), "0;2430;2425");
    EQ(amd::tuningStatus(), "ok\t0;0;60;0");

    Fake t;   // after a trip, the app's revert clears what tripped
    setup(t);
    EQ(amd::tuningApply(0, 5, none, none, 83), "ok\t0;2430;2425");
    seconds(31);
    EQ(amd::tuningStatus(), "ok\t0;0;60;timeout");
    EQ(amd::tuningReset(0), "ok\t");
    EQ(amd::tuningStatus(), "ok\t0;0;60;0");
}

int main() {
    testNoAmd();
    testOldDriver();
    testInfo();
    testReadouts();
    testUnsupported();
    testRangeAndStep();
    testApplyFailure();
    testBusyKeepTimeout();
    testWatch();
    testExit();
    testProfile();
    testAuto();
    testRevertReset();
    if (failed) printf("helper amd: %d of %d checks failed\n", failed, checks);
    else printf("helper amd: all %d checks passed\n", checks);
    return failed ? 1 : 0;
}
