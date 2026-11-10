// Stepping down when the GPU cannot keep up: GpuFit decides, Pacer lowers the output rate.
// Build: g++ -std=c++20 -O2 -I../src fit_test.cpp -o fit_test
#include "GpuFit.h"
#include "Pacer.h"
#include "Settings.h"
#include <cmath>
#include <cstdio>

using namespace sbfg;

static int failures = 0;
static void check(bool ok, const char* what) {
    std::printf("%-58s %s\n", what, ok ? "ok" : "FAILED");
    if (!ok) ++failures;
}

// Stats lines every 0.5 s, misses per second as given. Returns when it fired, or -1.
static double firstStep(double missPerSec, double seconds) {
    GpuFit fit;
    double owed = 0;
    for (double t = 0; t < seconds; t += 0.5) {
        owed += missPerSec * 0.5;
        if (fit.update(t, uint64_t(owed))) return t;
    }
    return -1;
}

static void feed(Pacer& p, double t) {
    p.onArrival(t);
    p.onSource(t, 0.002);
}

static Pacer fed(MulMode mode, double hz, double fps, int maxMul = 4) {
    PacerConfig c;
    c.mode = mode;
    c.refreshHz = hz;
    c.maxMul = maxMul;
    Pacer p(c);
    double t = 1;
    for (int i = 0; i < 400; ++i, t += 1.0 / fps) feed(p, t);
    return p;
}

static Pacer aimed(MulMode mode, double hz, double fps, double target, int maxMul = 4) {
    PacerConfig c;
    c.mode = mode;
    c.refreshHz = hz;
    c.maxMul = maxMul;
    c.targetFps = target;
    Pacer p(c);
    double t = 1;
    for (int i = 0; i < 400; ++i, t += 1.0 / fps) feed(p, t);
    return p;
}

static bool near(double a, double b) { return std::abs(a - b) < b * 0.01; }

// Stats lines every 0.5 s with the output under the floor for the first `under` seconds.
static double firstBehind(double under, double seconds) {
    GpuFit fit;
    for (double t = 0; t < seconds; t += 0.5)
        if (fit.behind(t, t < under ? 30 : 60, 40)) return t;
    return -1;
}

int main() {
    check(near(aimed(MulMode::Auto, 144, 50, 0).outputFps(), 144), "auto, no rate chosen: the monitor");
    check(near(aimed(MulMode::Auto, 144, 50, 72).outputFps(), 72), "auto 72 on 144 Hz: on the vblank grid");
    check(near(aimed(MulMode::Auto, 144, 50, 100).outputFps(), 100), "auto 100 on 144 Hz: between vblanks");
    check(near(aimed(MulMode::Auto, 144, 100, 60).outputFps(), 60), "auto 60 on 144 Hz: lower than the game");
    check(near(aimed(MulMode::Auto, 60, 54, 120).outputFps(), 120), "auto 120 on 60 Hz: passes the monitor");
    check(near(aimed(MulMode::Auto, 60, 54, 300).outputFps(), 216), "auto 300 on 60 Hz: stops at source x4");
    check(near(aimed(MulMode::X2, 60, 54, 0).outputFps(), 60), "x2 on 60 Hz, no rate chosen: the monitor");
    check(near(aimed(MulMode::X2, 60, 54, 120).outputFps(), 108), "x2 with 120: stops at source x2");
    check(near(aimed(MulMode::X4, 60, 54, 120).outputFps(), 120), "x4 with 120: stops at the chosen rate");
    check(near(aimed(MulMode::X8, 144, 30, 90).outputFps(), 90), "x8 with 90 on 144 Hz");
    check(aimed(MulMode::Auto, 60, 54, 120).passesRefresh(), "120 on 60 Hz goes around vsync");
    check(!aimed(MulMode::Auto, 144, 54, 120).passesRefresh(), "120 on 144 Hz keeps vsync");
    check(!aimed(MulMode::Auto, 60, 54, 60).passesRefresh(), "60 on 60 Hz keeps vsync");
    {   // the clock runs at the chosen rate, not on vblanks
        Pacer p = aimed(MulMode::Auto, 60, 80, 240);
        const double a = p.next().displayAt;
        p.advance(a - 0.01);
        check(near(p.next().displayAt - a, 1.0 / 240), "240 on 60 Hz: slots 1/240 s apart");
    }
    {   // chosen while running
        Pacer p = fed(MulMode::Auto, 60, 54);
        p.setTarget(120);
        check(near(p.outputFps(), 120), "rate set while running");
        p.setTarget(0);
        double t = 100;
        for (int i = 0; i < 100; ++i, t += 1.0 / 54) feed(p, t);
        check(near(p.outputFps(), 60), "rate cleared while running");
    }
    {   // the GPU cannot hold it
        Pacer p = aimed(MulMode::Auto, 60, 80, 240);
        check(p.slowDown() && near(p.outputFps(), 180), "a chosen rate steps down too");
    }
    check(firstBehind(0, 30) < 0, "output above the floor stays");
    check(firstBehind(2, 30) < 0, "2 s under the floor stays");
    const double b = firstBehind(30, 30);
    check(b >= 2.5 && b <= 3.5, "under the floor steps down after about 3 s");
    {   // no step left: a run that stays under the floor still ends, in a few steps' time
        GpuFit fit;
        int steps = 0;
        for (double s2 = 0; s2 < 12; s2 += 0.5) steps += fit.behind(s2, 8, 40);
        check(steps >= 3, "under the floor keeps stepping, about every 3.5 s");
    }
    {   // a step just taken needs a moment: the pacer starts over after a quality change
        GpuFit fit;
        double second = -1;
        bool first = false;
        for (double s2 = 0; s2 < 12 && second < 0; s2 += 0.5) {
            const bool hit = fit.behind(s2, 8, 40);
            if (hit && first) second = s2;
            first = first || hit;
        }
        check(second >= 6, "no second step before the first one has settled");
    }

    check(firstStep(0, 60) < 0, "no misses stays");
    check(firstStep(1, 60) < 0, "one miss a second stays");
    const double t = firstStep(4, 60);
    check(t >= 3 && t <= 4.5, "4 misses a second steps down after about 3 s");
    const double t2 = firstStep(30, 60);
    check(t2 >= 1.5 && t2 <= 2.5, "30 misses a second steps down after about 2 s");
    {   // a card far behind reaches the lowest step quickly
        GpuFit fit;
        uint64_t skipped = 0;
        int steps = 0;
        for (double s2 = 0; s2 < 9; s2 += 0.5) {
            skipped += 15;
            steps += fit.update(s2, skipped);
        }
        check(steps >= 3, "30 misses a second: three steps within 9 s");
    }

    {   // the game's own rate, read before generation starts
        GameAlone a;
        check(!a.reading(1, 0) && a.fps(1) == 0, "not started: nothing to compare with");
        a.start(10, 500);
        check(a.reading(10.5, 520), "reads for a second");
        check(!a.reading(11, 540), "then generation starts");
        check(near(a.fps(11), 40), "40 frames in a second read as 40 fps");
        check(near(a.fps(25), 40), "still compared 14 s later");
        check(a.fps(27) == 0, "not compared after that");
        a.start(30, 800);
        check(a.reading(30.2, 805) && a.fps(30.2) == 0, "a new reading replaces the old one");
    }
    {   // one hitch in the middle of a healthy run
        GpuFit fit;
        bool fired = false;
        uint64_t skipped = 0;
        for (double s = 0; s < 30; s += 0.5) {
            if (s == 10) skipped += 6;
            fired = fit.update(s, skipped) || fired;
        }
        check(!fired, "a single hitch of 6 misses stays");
    }
    {   // keeps stepping while the overload lasts, with room to see each step's effect
        GpuFit fit;
        uint64_t skipped = 0;
        int steps = 0;
        for (double s = 0; s < 30; s += 0.5) {
            skipped += 3;
            steps += fit.update(s, skipped);
        }
        check(steps >= 3 && steps <= 6, "lasting overload steps several times, not every line");
    }
    {   // Auto at 144 Hz: 144 -> 72 -> 48, then the source rate is reached
        Pacer p = fed(MulMode::Auto, 144, 40);
        check(p.outputFps() > 143, "auto starts at the refresh rate");
        check(p.slowDown(), "auto can slow down");
        for (double t2 = 11; t2 < 14; t2 += 1.0 / 40) feed(p, t2);
        check(p.outputFps() < 73 && p.outputFps() > 71, "auto drops to half the refresh rate");
        check(p.slowDown(), "auto can slow down again");
        for (double t2 = 14; t2 < 17; t2 += 1.0 / 40) feed(p, t2);
        check(p.outputFps() < 49 && p.outputFps() > 47, "then to a third, still on vblanks");
        check(!p.slowDown(), "stops near the source rate");
    }
    {   // fixed x3 is capped, never above what it would output anyway
        Pacer p = fed(MulMode::X3, 144, 40);
        const double before = p.outputFps();
        check(before > 119 && before < 121, "x3 outputs three times the source");
        check(p.slowDown(), "x3 can slow down");
        check(p.outputFps() < before * 0.8, "x3 output is capped");
        p.setConfig(p.config());
        for (double t2 = 20; t2 < 22; t2 += 1.0 / 40) feed(p, t2);
        check(p.outputFps() > 119, "changing the mode clears the cap");
    }
    {   // a slow game: the limit decides between every vblank and every second one
        check(fed(MulMode::Auto, 144, 25).outputFps() < 73, "25 fps, limit 4: half the refresh rate");
        check(fed(MulMode::Auto, 144, 25, 8).outputFps() > 143, "25 fps, limit 8: every vblank");
        check(fed(MulMode::Auto, 144, 15, 8).outputFps() > 143, "15 fps, limit 8: every vblank, 25% over the limit");
        check(fed(MulMode::Auto, 144, 12, 8).outputFps() < 73, "12 fps, limit 8: half the refresh rate");
    }
    {   // fixed x6 and x8: that many times the source, up to the refresh rate
        const auto near = [](double a, double b) { return a > b - 1 && a < b + 1; };
        check(near(fed(MulMode::X6, 144, 20).outputFps(), 120), "x6 at 20 fps outputs 120");
        check(near(fed(MulMode::X8, 144, 15).outputFps(), 120), "x8 at 15 fps outputs 120");
        check(near(fed(MulMode::X8, 144, 12).outputFps(), 96), "x8 at 12 fps outputs 96");
        check(near(fed(MulMode::X6, 144, 30).outputFps(), 144), "x6 at 30 fps stops at the refresh rate");
        check(near(fed(MulMode::X8, 120, 60).outputFps(), 120), "x8 at 60 fps stops at the refresh rate");
        Settings s;
        check(parseArgs({"--hwnd", "1", "--mode", "x6"}, s).empty() && s.mode == MulMode::X6, "--mode x6 parses");
        check(parseArgs({"--hwnd", "1", "--mode", "x8"}, s).empty() && s.mode == MulMode::X8, "--mode x8 parses");
        check(!parseArgs({"--hwnd", "1", "--mode", "x5"}, s).empty(), "--mode x5 is refused");
    }
    {   // a frame kept waiting: frames that were never used still set the rate
        PacerConfig c;
        c.refreshHz = 144;
        Pacer p(c);
        double t2 = 1;
        for (int i = 0; i < 300; ++i, t2 += 1.0 / 60) {
            p.onArrival(t2);
            if (i % 8 == 0) p.onSource(t2, 0.03);
        }
        check(p.sourceFps() > 59 && p.sourceFps() < 61, "source stays 60 fps while one frame in 8 is used");
        check(p.divider() == 1, "and the output stays on every vblank");
        p.reset();
        p.onArrival(5);
        p.onArrival(5.1, 3);
        check(p.sourceFps() > 29 && p.sourceFps() < 31, "three frames in 100 ms read as 30 fps");
    }
    {   // fewer frames on screen than the game alone: the overlay goes on the second stats line
        GpuFit fit;
        check(!fit.dropped(0.0, 5, 130), "under the game's rate: not on the first line");
        check(fit.dropped(0.5, 5, 130), "under the game's rate: the second line in a row takes the overlay away");
        GpuFit ok;
        bool any = false;
        for (double sec = 0; sec < 10; sec += 0.5) any = any || ok.dropped(sec, 130, 130);
        check(!any, "at the floor is not under it");
        GpuFit dip;
        dip.dropped(0.0, 5, 130);
        dip.dropped(0.5, 140, 130);
        check(!dip.dropped(1.0, 5, 130), "one good line in between starts the count again");
        GpuFit none;
        check(!none.dropped(0, 0, 0) && !none.dropped(0.5, 0, 0), "no game rate, nothing to compare");
        GpuFit again;
        again.dropped(0.0, 5, 130);
        again.settle(0.2);
        check(!again.dropped(0.5, 5, 130), "a new start forgets the line before it");
    }
    {   // the trial run nobody sees, and what keeps generated frames on screen (measured rates only)
        // beside a foreground game filling the GPU: alone 35; with the engine source 30, output 60
        check(TrialPasses(60, 30, 35), "60 against 35 alone goes on screen");
        // the same game with the engine starved of GPU time: source read as 8, output 4
        check(!TrialPasses(4, 8, 35), "4 against 35 alone stays unseen");
        check(!TrialPasses(38, 31, 35), "38 against 35 alone is not enough of a gain");
        // a light game held at 100 fps by its own limit, 144 Hz
        check(TrialPasses(144, 100, 100), "144 against a game capped at 100 goes on screen");
        // generation that costs more than it adds: alone 50, 2x of the 20 fps left
        check(!TrialPasses(40, 20, 50), "40 from a game that made 50 alone stays unseen");
        check(!TrialPasses(0, 0, 60), "a game standing still has no verdict");
        check(TrialPasses(60, 30, 0) && !TrialPasses(33, 30, 0), "alone not known: the source rate stands in");
        const auto about = [](double a, double b) { return std::abs(a - b) < 0.01; };
        check(about(ShownFloor(30, 60), 34.5), "30 fps on 60 Hz: 34.5 on screen or the overlay goes");
        check(about(ShownFloor(100, 144), 115), "100 fps on 144 Hz: 115");
        check(about(ShownFloor(130, 144), 136.8), "130 fps on 144 Hz: a full screen is enough (136.8)");
        NearLimit n;
        check(!n.restNow(100, 144) && !n.resting(), "100 fps on 144 Hz is not at the limit");
        check(n.restNow(130, 144) && n.resting(), "130 fps on 144 Hz rests at once when asked");
    }
    {   // a game already near the limit rests; generation starts when it falls clear of it
        NearLimit n;
        check(n.judgeAlone(135, 144) && n.resting(), "135 fps alone on 144 Hz rests");
        check(!NearLimit().judgeAlone(100, 144), "100 fps alone on 144 Hz generates");
        check(NearLimit().judgeAlone(55, 60), "55 fps alone on 60 Hz rests");
        check(!NearLimit().judgeAlone(50, 60), "50 fps alone on 60 Hz generates (60 is 1.2 times it)");
        check(NearLimit().judgeAlone(100, 60), "a game faster than the rate chosen rests");
        check(!NearLimit().judgeAlone(135, 240), "135 fps with 240 chosen generates");
        bool changed = false;
        for (double sec = 0; sec < 1.9; sec += 0.5) changed = changed || n.update(sec, 90, 144);
        check(!changed && n.resting(), "resting: under 2 s below the line changes nothing");
        check(n.update(2.0, 90, 144) && !n.resting(), "resting: 2 s at 90 fps starts generation");
        changed = false;
        for (double sec = 3; sec < 20; sec += 0.5) changed = changed || n.update(sec, 110, 144);
        check(!changed && !n.resting(), "generating at 110 fps stays generating");
        for (double sec = 20; sec < 22.9; sec += 0.5) changed = changed || n.update(sec, 130, 144);
        check(!changed, "generating: under 3 s near the limit changes nothing");
        check(n.update(23.0, 130, 144) && n.resting(), "generating: 3 s at 130 fps rests");
        changed = false;
        for (double sec = 24; sec < 40; sec += 0.5) changed = changed || n.update(sec, 120, 144);
        check(!changed && n.resting(), "resting at 120 fps stays resting");
        NearLimit gap;
        gap.judgeAlone(135, 144);
        gap.update(0, 90, 144);
        gap.update(1.0, 130, 144);
        check(!gap.update(2.5, 90, 144), "a line back over the limit starts the wait again");
    }
    {   // GPU priority option, and an output slot that was left out
        Settings s;
        check(parseArgs({"--hwnd", "1"}, s).empty() && s.gpuPriority == kGpuPriorityAuto, "GPU priority is auto unless said");
        check(parseArgs({"--hwnd", "1", "--gpu-priority", "off"}, s).empty() && s.gpuPriority == kGpuPriorityOff,
              "--gpu-priority off parses");
        check(parseArgs({"--hwnd", "1", "--gpu-priority", "5"}, s).empty() && s.gpuPriority == 5, "--gpu-priority 5 parses");
        check(!parseArgs({"--hwnd", "1", "--gpu-priority", "9"}, s).empty(), "--gpu-priority 9 is refused");
        Pacer p = fed(MulMode::X2, 144, 60);
        const bool first = p.repeats(0.5f), again = p.repeats(0.5f);
        p.forgetShown();
        check(!first && again && !p.repeats(0.5f), "an image left out is not counted as shown");
    }
    return failures ? 1 : 0;
}
