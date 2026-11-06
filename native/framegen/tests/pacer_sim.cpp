// Frame pacing simulation on a vsync display model.
// Same jittery source timeline for every scheduler:
//   burst : generated frames presented immediately after computing (t = k/N)
//   fixed : even spacing from the last interval, t = k/N  (typical first version)
//   v0.6  : sbfg::Pacer display clock, t from timestamps, adaptive delay D
// Metrics per displayed vblank:
//   judder_ms : stddev of (scan-out time - shown game time) per vblank (0 = perfectly even motion)
//   repeat%   : vblanks with no new frame (visible as a hitch)
//   drop%     : presented frames never shown (two in one vblank = wasted GPU work)
//   lat_ms    : mean (scan-out time - game time of the shown content)
// Build: g++ -std=c++20 -O2 -I../src pacer_sim.cpp -o pacer_sim
#include "Pacer.h"
#include <cstdio>
#include <map>
#include <random>
#include <string>
#include <vector>

using namespace sbfg;

struct Shown { double content; };
struct Present { double time; double content; };

struct Result { double judder, repeat, drop, lat, outFps; uint64_t extra; };

static Result evaluate(const std::vector<Present>& pres, double hz, double t0, double t1) {
    const double p = 1.0 / hz;
    std::map<long, double> shown;
    size_t dropped = 0;
    for (auto& x : pres) {
        long vb = long(std::ceil(x.time / p - 1e-9));
        if (shown.count(vb)) ++dropped;
        shown[vb] = x.content;
    }
    long first = long(std::ceil((t0 + 1.0) / p)), last = long(t1 / p) - 2;
    double cur = -1, s = 0, s2 = 0, lat = 0; long n = 0, rep = 0, ln = 0, fresh = 0;
    for (long vb = shown.begin()->first; vb <= last; ++vb) {
        auto it = shown.find(vb);
        if (it != shown.end()) cur = it->second;
        if (vb < first || cur < 0) continue;
        if (it == shown.end()) ++rep; else ++fresh;
        lat += vb * p - cur; ++ln;
        { double d = (vb * p - cur) * 1000; s += d; s2 += d * d; ++n; }
    }
    double m = s / n;
    return {std::sqrt(std::max(0.0, s2 / n - m * m)), 100.0 * rep / (last - first + 1),
            100.0 * dropped / pres.size(), lat / ln * 1000, fresh / ((last - first + 1) * p), 0};
}

static std::vector<Present> runFixed(const std::vector<double>& a, double hz, double proc,
                                     int fixedN, bool burst, uint64_t& switches) {
    std::vector<Present> out; int lastN = 0;
    for (size_t i = 1; i < a.size(); ++i) {
        double tA = a[i - 1], tB = a[i], I = tB - tA;
        double next = i + 1 < a.size() ? a[i + 1] : 1e9;
        int N = fixedN ? fixedN : std::clamp(int(std::lround(hz * I)), 1, 4);
        if (lastN && N != lastN) ++switches;
        lastN = N;
        for (int k = 1; k <= N; ++k) {
            double t = burst ? tB + proc * k : tB + proc + (k - 1) * I / N;
            if (t < next + proc) out.push_back({t, tA + double(k) / N * I});
        }
    }
    return out;
}

static std::vector<Present> runPacer(const std::vector<double>& a, double hz, double proc,
                                     MulMode mode, Pacer::Stats& st, double& delayMs) {
    PacerConfig c; c.mode = mode; c.refreshHz = hz; c.vblankPhase = 0;
    Pacer pc(c);
    std::vector<Present> out;
    size_t i = 0;
    double tA = 0, tB = 0, dsum = 0; long dn = 0;
    while (i < a.size()) {
        bool srcFirst = (!pc.active() || a[i] <= pc.next().presentAt) && pc.wantsSource();
        if (srcFirst) {
            tA = tB; tB = a[i];
            pc.onArrival(a[i]);
            pc.onSource(a[i], proc);
            ++i;
            continue;
        }
        OutputSlot s = pc.next();
        float t = pc.tFor(s.displayAt);
        double content = tA + t * (tB - tA);
        out.push_back({s.presentAt + proc, content});  // finished = presentAt + proc
        dsum += pc.delayMs(); ++dn;
        pc.advance(s.presentAt + proc);
    }
    st = pc.stats; delayMs = dsum / dn;
    return out;
}

static std::vector<double> makeArrivals(double f0, double f1, double sec, double jitMs,
                                        double spikeP, unsigned seed) {
    std::mt19937 rng(seed);
    std::normal_distribution<double> jit(0, jitMs / 1000);
    std::uniform_real_distribution<double> u(0, 1);
    std::vector<double> a; double t = 1.0;
    while (t < 1.0 + sec) {
        double prog = (t - 1.0) / sec;
        double fps = f0 + (f1 - f0) * 0.5 * (1 - std::cos(prog * 6.2832 * 2));
        double d = 1.0 / fps + jit(rng);
        if (u(rng) < spikeP) d += 0.025;
        t += std::max(0.004, d);
        a.push_back(t);
    }
    return a;
}

int main() {
    const double hz = 144, proc = 0.0025;
    struct Case { const char* name; double f0, f1; int fixedN; MulMode mode; };
    Case cases[] = {
        {"48fps,  x3", 48, 48, 3, MulMode::X3},
        {"48fps,  auto", 48, 48, 0, MulMode::Auto},
        {"40-70fps, auto", 40, 70, 0, MulMode::Auto},
        {"30fps,  x4", 30, 30, 4, MulMode::X4},
        {"15fps,  x8", 15, 15, 8, MulMode::X8},
        {"33-37fps, auto", 33, 37, 0, MulMode::Auto},   // at the 144 Hz divider limit
    };
    std::printf("%-16s %-6s %9s %8s %6s %7s %7s %s\n", "case", "sched", "judder_ms", "repeat%",
                "drop%", "lat_ms", "outFPS", "notes");
    for (auto& c : cases) {
        auto a = makeArrivals(c.f0, c.f1, 20.0, 1.5, 0.03, 42);
        for (int k = 0; k < 3; ++k) {
            std::vector<Present> p; char note[96] = "";
            uint64_t sw = 0;
            if (k < 2) {
                p = runFixed(a, hz, proc, c.fixedN, k == 0, sw);
                if (!c.fixedN) std::snprintf(note, sizeof note, "N switches=%llu", (unsigned long long)sw);
            } else {
                Pacer::Stats st; double d;
                p = runPacer(a, hz, proc, c.mode, st, d);
                std::snprintf(note, sizeof note, "late=%llu skipped=%llu switches=%llu",
                              (unsigned long long)st.late, (unsigned long long)st.skipped,
                              (unsigned long long)st.switches);
            }
            Result r = evaluate(p, hz, a.front(), a.back());
            const char* nm[] = {"burst", "fixed", "v0.6"};
            std::printf("%-16s %-6s %9.2f %8.1f %6.1f %7.1f %7.1f %s\n", c.name, nm[k], r.judder,
                        r.repeat, r.drop, r.lat, r.outFps, note);
        }
    }
}
