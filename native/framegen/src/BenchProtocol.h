// Benchmark mode (--bench, --bench-load): the stdout lines and the batch planning.
// Header-only, no Windows dependencies (unit-tested without a GPU: tests/bench_test.cpp).
//
// Lines (UTF-8, one JSON object per line, numbers in invariant form):
//   {"type":"bench","quality":"balanced","width":2560,"height":1440,"analyzeMs":1.234,"genMs":0.456,"gpu":"..."}
//   {"type":"bench_load","sec":1,"framesPerSec":812.5,"gpu":"..."}
//   {"type":"bench_done"}
//   {"type":"error","message":"..."}
#pragma once
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

namespace sbfg::bench {

// Fixed decimals, '.' always (std::to_chars does not look at the locale). Not a number: 0.
inline std::string Num(double v, int decimals) {
    if (!std::isfinite(v)) v = 0;
    const double scale = std::pow(10.0, decimals);
    double r = std::round(v * scale) / scale;
    if (r == 0) r = 0;   // no "-0.000"
    char buf[64];
    const auto res = std::to_chars(buf, buf + sizeof buf, r, std::chars_format::fixed, decimals);
    return res.ec == std::errc() ? std::string(buf, res.ptr) : std::string("0");
}

// A JSON string with its quotes. UTF-8 passes through; control characters become \u00XX.
inline std::string Str(const std::string& s) {
    std::string o = "\"";
    for (const char c : s) {
        const unsigned char u = static_cast<unsigned char>(c);
        if (c == '"' || c == '\\') { o += '\\'; o += c; }
        else if (u < 0x20) {
            char b[8];
            std::snprintf(b, sizeof b, "\\u%04x", unsigned(u));
            o += b;
        } else o += c;
    }
    return o + "\"";
}

inline std::string BenchLine(const char* quality, unsigned w, unsigned h, double analyzeMs, double genMs,
                             const std::string& gpu) {
    return "{\"type\":\"bench\",\"quality\":" + Str(quality) + ",\"width\":" + std::to_string(w) +
           ",\"height\":" + std::to_string(h) + ",\"analyzeMs\":" + Num(analyzeMs, 3) + ",\"genMs\":" +
           Num(genMs, 3) + ",\"gpu\":" + Str(gpu) + "}";
}

inline std::string LoadLine(int sec, double framesPerSec, const std::string& gpu) {
    return "{\"type\":\"bench_load\",\"sec\":" + std::to_string(sec) + ",\"framesPerSec\":" + Num(framesPerSec, 1) +
           ",\"gpu\":" + Str(gpu) + "}";
}

inline std::string DoneLine() { return "{\"type\":\"bench_done\"}"; }

inline std::string ErrorLine(const std::string& message) {
    return "{\"type\":\"error\",\"message\":" + Str(message) + "}";
}

inline double Median(std::vector<double> v) {
    if (v.empty()) return 0;
    std::sort(v.begin(), v.end());
    const size_t n = v.size();
    return n % 2 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
}

// --- --bench: time plan --------------------------------------------------------
// The whole run must end within 15 s. Measuring is planned to end by kPlanSec after the start; a
// watchdog ends the process with an error at kLimitSec.
constexpr double kPlanSec = 12.5;
constexpr double kLimitSec = 15.0;
constexpr double kWarmupSec = 0.5;        // balanced passes before the first measurement: clocks up
constexpr double kMaxQualitySec = 2.2;    // a quality never takes longer than this
constexpr int kRounds = 9;                // batches of each kind per quality (median)
constexpr int kMaxAnalyses = 20, kMaxGenerates = 60;  // per batch, as tests/gen_bench.cpp had them

// Seconds one quality may take: an equal share of what is left of the plan.
inline double QualityBudgetSec(double elapsedSec, int qualitiesLeft) {
    if (qualitiesLeft <= 0) return 0;
    return std::clamp((kPlanSec - elapsedSec) / qualitiesLeft, 0.0, kMaxQualitySec);
}

// GPU timestamps (ticks) of a batch's start and end in ms; -1 when they cannot be trusted: the GPU
// clock changed in between (disjoint), no frequency, or the end before the start.
inline double TicksToMs(unsigned long long begin, unsigned long long end, unsigned long long frequency,
                        bool disjoint) {
    if (disjoint || frequency == 0 || end < begin) return -1;
    return double(end - begin) * 1000.0 / double(frequency);
}

// One more round of batches? The first always runs; then while one more like the last still fits.
inline bool NextRound(int round, double elapsedSec, double lastRoundSec, double budgetSec) {
    if (round >= kRounds) return false;
    return round == 0 || elapsedSec + lastRoundSec <= budgetSec;
}

// Can the next quality still start? It costs at least about what the last one did (a little more:
// the next is the finer one), and the run has to end before kLimitSec.
inline bool QualityFits(double elapsedSec, double lastQualitySec) {
    return elapsedSec + 1.3 * lastQualitySec <= kLimitSec - 0.5;
}

// How many analyses and generated frames go into one GPU-waited batch. A fast GPU gets the full
// batches (20 and 60: long enough that the wait at the end is a small part); a slow one gets fewer,
// so kRounds rounds of both fit in the budget. Never fewer than one.
struct BatchPlan { int analyses = 1, generates = 1; };
inline BatchPlan PlanBatches(double analyzeMsEach, double genMsEach, double budgetSec) {
    const double perBatchMs = std::max(0.0, budgetSec) * 1000.0 / (2.0 * kRounds);
    auto count = [&](double each, int most) {
        if (!(each > 0) || !std::isfinite(each)) return most;
        return int(std::clamp(std::floor(perBatchMs / each), 1.0, double(most)));
    };
    return {count(analyzeMsEach, kMaxAnalyses), count(genMsEach, kMaxGenerates)};
}

// --- --bench-load -----------------------------------------------------------------
// One cycle = one analysed game frame and three generated frames (what 4x does per game frame).
// framesPerSec counts the generated frames. Batches of about kLoadBatchMs of GPU work, kLoadInFlight
// of them queued, so the GPU never waits for this process.
constexpr int kLoadGenPerCycle = 3;
constexpr double kLoadBatchMs = 15.0;
constexpr int kLoadInFlight = 3;
constexpr int kLoadMaxCycles = 64;
constexpr double kLoadExtraSec = 12.0;    // watchdog: S seconds of load plus this for setup and the end

inline int LoadCyclesPerBatch(double cycleMs) {
    if (!(cycleMs > 0) || !std::isfinite(cycleMs)) return 1;
    return int(std::clamp(std::round(kLoadBatchMs / cycleMs), 1.0, double(kLoadMaxCycles)));
}

// Generated frames per second between two batch completions (frames completed after the first).
inline double FramesPerSec(double frames, double fromSec, double toSec) {
    return toSec > fromSec && frames >= 0 ? frames / (toSec - fromSec) : 0;
}

}  // namespace sbfg::bench
