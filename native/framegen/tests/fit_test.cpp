// Stepping down when the GPU cannot keep up: GpuFit decides, Pacer lowers the output rate.
// Build: g++ -std=c++20 -O2 -I../src fit_test.cpp -o fit_test
#include "GpuFit.h"
#include "Pacer.h"
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

static Pacer fed(MulMode mode, double hz, double fps) {
    PacerConfig c;
    c.mode = mode;
    c.refreshHz = hz;
    Pacer p(c);
    double t = 1;
    for (int i = 0; i < 400; ++i, t += 1.0 / fps) feed(p, t);
    return p;
}

int main() {
    check(firstStep(0, 60) < 0, "no misses stays");
    check(firstStep(1, 60) < 0, "one miss a second stays");
    const double t = firstStep(4, 60);
    check(t >= 3 && t <= 4.5, "4 misses a second steps down after about 3 s");

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
    return failures ? 1 : 0;
}
