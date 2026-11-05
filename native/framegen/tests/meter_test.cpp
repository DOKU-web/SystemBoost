// FrameMeter checks: rates come from timestamps, not from how often take() is called.
//   g++ -std=c++20 -O2 -Wall -Wextra -Isrc tests/meter_test.cpp -o meter_test
#include "FrameMeter.h"
#include <cmath>
#include <cstdio>

using sbfg::FrameMeter;

static int g_failed = 0;

static void Expect(const char* what, double got, double want, double tol) {
    const bool ok = std::abs(got - want) <= tol;
    std::printf("%-44s %9.3f want %9.3f %s\n", what, got, want, ok ? "ok" : "FAIL");
    g_failed += !ok;
}

// Feeds src and out frames for `seconds` of simulated time, calling take() every statsSec.
struct Feed {
    FrameMeter m;
    double now = 100;
    double srcNext = 100, outNext = 100;
    FrameMeter::Sample last;
    int takes = 0;
    void run(double seconds, double srcFps, double outFps, double statsSec) {
        const double end = now + seconds;
        double nextTake = now + statsSec;
        while (now < end) {
            now += 0.0005;
            while (srcNext <= now) { m.onSource(); srcNext += 1 / srcFps; }
            while (outNext <= now) { m.onOutput(false); outNext += 1 / outFps; }
            if (now >= nextTake) {
                nextTake += statsSec;
                FrameMeter::Sample s;
                if (m.take(now, s)) { last = s; ++takes; }
            }
        }
    }
};

int main() {
    {   // steady rate
        Feed f;
        f.m.reset(f.now);
        f.run(5, 40, 80, 0.5);
        Expect("40 -> 80: source fps", f.last.srcFps(), 40, 1);
        Expect("40 -> 80: output fps", f.last.outFps(), 80, 1);
        Expect("40 -> 80: window seconds", f.last.seconds, 1, 0.02);
        Expect("40 -> 80: readings", f.takes, 8, 1);
    }
    {   // the interval between readings does not change the value
        Feed f;
        f.m.reset(f.now);
        f.run(5, 60, 144, 0.1);
        Expect("60 -> 144, every 100 ms: output fps", f.last.outFps(), 144, 2);
        Expect("60 -> 144, every 100 ms: window seconds", f.last.seconds, 1, 0.11);
    }
    {   // a reading comes late: elapsed time, not the number of readings, sets the rate
        Feed f;
        f.m.reset(f.now);
        f.run(3, 40, 80, 0.5);
        f.run(3, 40, 80, 3.0);
        Expect("late reading: output fps", f.last.outFps(), 80, 1);
        Expect("late reading: window seconds", f.last.seconds, 3, 0.02);
    }
    {   // lost readings: the totals still give the exact average
        Feed f;
        f.m.reset(f.now);
        f.run(2, 40, 80, 0.5);
        const double src0 = double(f.m.totalSrc()), out0 = double(f.m.totalOut()), sec0 = f.m.activeSec();
        f.run(60, 40, 80, 0.5);
        const double dt = f.m.activeSec() - sec0;
        Expect("totals: source fps", (double(f.m.totalSrc()) - src0) / dt, 40, 0.1);
        Expect("totals: output fps", (double(f.m.totalOut()) - out0) / dt, 80, 0.1);
    }
    {   // the backlog right after a start stays out of the first reading
        FrameMeter m;
        double now = 100;
        m.reset(now);
        for (int i = 0; i < 30; ++i) m.onSource();
        now += 0.5;
        FrameMeter::Sample s;
        Expect("backlog: first window gives no reading", m.take(now, s) ? 1 : 0, 0, 0);
        for (int w = 0; w < 2; ++w) {
            for (int i = 0; i < 20; ++i) m.onSource();
            now += 0.5;
            const bool ok = m.take(now, s);
            Expect("backlog: reading after the second window", ok ? 1 : 0, w == 1 ? 1 : 0, 0);
        }
        Expect("backlog: first rate", s.srcFps(), 40, 0.5);
    }
    {   // a pause adds no active time and restarts the settling
        Feed f;
        f.m.reset(f.now);
        f.run(3, 40, 80, 0.5);
        f.m.pause(f.now);
        const double sec = f.m.activeSec();
        f.now += 30;
        f.srcNext = f.outNext = f.now;
        f.m.reset(f.now);
        Expect("pause: active seconds kept", f.m.activeSec(), sec, 1e-9);
        f.run(3, 40, 80, 0.5);
        Expect("pause: active seconds after", f.m.activeSec() - sec, 3, 0.55);
        Expect("pause: output fps after", f.last.outFps(), 80, 1);
    }
    std::printf("%s\n", g_failed ? "FAILED" : "all ok");
    return g_failed ? 1 : 0;
}
