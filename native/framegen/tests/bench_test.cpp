// Benchmark mode without a GPU: command line, the stdout lines, and the 15 s time plan.
// Build: cmake -DSBFG_TESTS=ON (target bench_test), or g++ -std=c++20 -O2 -Wall -Wextra -Isrc tests/bench_test.cpp
#include "BenchProtocol.h"
#include "Settings.h"
#include <clocale>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

using namespace sbfg;
using namespace sbfg::bench;

static int failures = 0;
static void check(bool ok, const std::string& what) {
    std::printf("%-72s %s\n", what.c_str(), ok ? "ok" : "FAILED");
    if (!ok) ++failures;
}
static void same(const std::string& got, const std::string& want, const std::string& what) {
    check(got == want, what);
    if (got != want) std::printf("    got  %s\n    want %s\n", got.c_str(), want.c_str());
}

static std::string parse(std::vector<std::string> a, Settings& s) {
    s = Settings{};
    return parseArgs(a, s);
}

static void commandLine() {
    Settings s;
    check(parse({"--bench", "--bench-width", "2560", "--bench-height", "1440"}, s).empty() && s.bench &&
              s.benchLoadSec == 0 && s.benchW == 2560 && s.benchH == 1440 && s.benchMode(),
          "--bench with a size");
    check(parse({"--bench-load", "30", "--bench-width", "1920", "--bench-height", "1080", "--gen-gpu", "1",
                 "--parent-pid", "4321", "--shader-dir", "C:\\x\\shaders"}, s).empty() &&
              !s.bench && s.benchLoadSec == 30 && s.genGpu == GenGpu::Index && s.genGpuIndex == 1 &&
              s.parentPid == 4321 && s.shaderDir == "C:\\x\\shaders" && s.hwnd == 0,
          "--bench-load with gpu, parent and shader folder");
    check(parse({"--bench", "--bench-width", "800", "--bench-height", "600", "--gen-gpu", "same"}, s).empty() &&
              s.genGpu == GenGpu::SameAsGame, "--gen-gpu same");
    check(parse({"--bench-load", "1", "--bench-width", "64", "--bench-height", "8192"}, s).empty(), "edges: 1 s, 64, 8192");
    check(parse({"--bench-load", "60", "--bench-width", "8192", "--bench-height", "64"}, s).empty(), "edges: 60 s");

    same(parse({"--bench"}, s), "--bench-width and --bench-height are required", "--bench without a size");
    same(parse({"--bench", "--bench-width", "1920"}, s), "--bench-width and --bench-height are required", "--bench without a height");
    same(parse({"--bench-load", "0", "--bench-width", "1920", "--bench-height", "1080"}, s), "--bench-load must be 1..60 seconds", "--bench-load 0");
    same(parse({"--bench-load", "61", "--bench-width", "1920", "--bench-height", "1080"}, s), "--bench-load must be 1..60 seconds", "--bench-load 61");
    same(parse({"--bench-load", "5x", "--bench-width", "1920", "--bench-height", "1080"}, s), "--bench-load must be 1..60 seconds", "--bench-load 5x");
    same(parse({"--bench-load", "-5", "--bench-width", "1920", "--bench-height", "1080"}, s), "--bench-load must be 1..60 seconds", "--bench-load -5");
    same(parse({"--bench-load"}, s), "missing value for --bench-load", "--bench-load without a value");
    same(parse({"--bench", "--bench-width", "63", "--bench-height", "1080"}, s), "--bench-width must be 64..8192", "width 63");
    same(parse({"--bench", "--bench-width", "1920", "--bench-height", "8193"}, s), "--bench-height must be 64..8192", "height 8193");
    same(parse({"--bench", "--bench-width", "1920.5", "--bench-height", "1080"}, s), "--bench-width must be 64..8192", "width 1920.5");
    same(parse({"--bench", "--bench-load", "5", "--bench-width", "1920", "--bench-height", "1080"}, s),
         "use --bench or --bench-load, not both", "both modes");
    same(parse({"--bench", "--list-gpus", "--bench-width", "1920", "--bench-height", "1080"}, s),
         "--bench cannot be combined with --list-gpus or --list-windows", "--bench with --list-gpus");
    same(parse({"--hwnd", "0x10", "--bench-width", "1920"}, s),
         "--bench-width and --bench-height need --bench or --bench-load", "a size without the benchmark");
    same(parse({"--bench", "--bench-width", "1920", "--bench-height", "1080", "--bench-foo"}, s),
         "missing value for --bench-foo", "unknown option in bench mode");
    same(parse({"--bench", "--bench-width", "1920", "--bench-height", "1080", "--parent-pid", "0"}, s),
         "bad --parent-pid 0", "bad parent pid");

    // the normal engine's command line reads as before
    check(parse({"--hwnd", "0x1234AB", "--mode", "x3", "--quality", "low"}, s).empty() && s.hwnd == 0x1234AB &&
              s.mode == MulMode::X3 && s.quality == Quality::Low && !s.benchMode(), "normal: --hwnd --mode --quality");
    check(parse({"--list-gpus"}, s).empty() && s.listGpus && !s.benchMode(), "normal: --list-gpus");
    same(parse({}, s), "--hwnd is required", "normal: no arguments");
    same(parse({"--hwnd", "1", "--quality", "ultra"}, s), "bad --quality ultra", "normal: bad quality");

    check(WantsBench({"--bench"}) && WantsBench({"--x", "--bench-load", "3"}), "WantsBench sees --bench and --bench-load");
    check(!WantsBench({"--hwnd", "1"}) && !WantsBench({"--bench-width", "1920"}) && !WantsBench({}),
          "WantsBench: not for other options");
    same(std::string(QualityName(Quality::Low)) + "," + QualityName(Quality::Performance) + "," +
             QualityName(Quality::Balanced) + "," + QualityName(Quality::Quality),
         "low,performance,balanced,quality", "quality names");
}

static void lines() {
    same(BenchLine("balanced", 2560, 1440, 1.234, 0.456, "NVIDIA GeForce RTX 3060"),
         "{\"type\":\"bench\",\"quality\":\"balanced\",\"width\":2560,\"height\":1440,\"analyzeMs\":1.234,"
         "\"genMs\":0.456,\"gpu\":\"NVIDIA GeForce RTX 3060\"}", "bench line");
    same(LoadLine(1, 812.5, "AMD Radeon RX 7800 XT"),
         "{\"type\":\"bench_load\",\"sec\":1,\"framesPerSec\":812.5,\"gpu\":\"AMD Radeon RX 7800 XT\"}", "bench_load line");
    same(DoneLine(), "{\"type\":\"bench_done\"}", "bench_done line");
    same(ErrorLine("shaders not found in C:\\a \"b\"\n"),
         "{\"type\":\"error\",\"message\":\"shaders not found in C:\\\\a \\\"b\\\"\\u000a\"}", "error line escapes");
    same(Str("Intel\xC2\xAE Arc\xE2\x84\xA2"), "\"Intel\xC2\xAE Arc\xE2\x84\xA2\"", "UTF-8 passes through");
    same(Str(std::string("a\tb\x01", 4)), "\"a\\u0009b\\u0001\"", "control characters");

    same(Num(1.23456, 3), "1.235", "3 decimals");
    same(Num(0.0004, 3), "0.000", "rounds to zero");
    same(Num(-0.0001, 3), "0.000", "no minus zero");
    same(Num(std::nan(""), 3), "0.000", "NaN is 0");
    same(Num(INFINITY, 1), "0.0", "infinity is 0");
    same(Num(812.46, 1), "812.5", "1 decimal");
    same(Num(1234567.0, 1), "1234567.0", "large");
    same(Num(2.0, 3), "2.000", "whole number keeps its decimals");

    // a locale with a decimal comma does not change the lines
    const char* names[] = {"de-DE", "de_DE.UTF-8", "German", "fr-FR", "fr_FR.UTF-8"};
    const char* got = nullptr;
    for (const char* n : names) if ((got = std::setlocale(LC_ALL, n))) break;
    if (got) {
        char buf[32];
        std::snprintf(buf, sizeof buf, "%.3f", 1.5);
        std::printf("  (locale %s: printf writes %s)\n", got, buf);
        same(Num(1.5, 3), "1.500", "invariant under a decimal-comma locale");
        same(BenchLine("low", 1920, 1080, 0.5, 0.25, "x"),
             "{\"type\":\"bench\",\"quality\":\"low\",\"width\":1920,\"height\":1080,\"analyzeMs\":0.500,"
             "\"genMs\":0.250,\"gpu\":\"x\"}", "bench line under that locale");
        std::setlocale(LC_ALL, "C");
    } else {
        std::printf("  (no decimal-comma locale on this system: skipped)\n");
    }
}

static void numbers() {
    check(Median({}) == 0, "median of nothing");
    check(Median({3, 1, 2}) == 2, "median, odd count");
    check(Median({4, 1, 3, 2}) == 2.5, "median, even count");
    check(Median({5, 1, 100, 2, 3, 4, 6, 7, 8}) == 5, "median ignores one outlier");

    BatchPlan p = PlanBatches(1.0, 0.3, kMaxQualitySec);
    check(p.analyses == 20 && p.generates == 60, "fast GPU: full batches of 20 and 60 (as gen_bench)");
    p = PlanBatches(30, 10, 2.2);
    check(p.analyses == 4 && p.generates == 12, "slow GPU: smaller batches (4 and 12)");
    p = PlanBatches(500, 200, 2.2);
    check(p.analyses == 1 && p.generates == 1, "very slow GPU: one of each");
    p = PlanBatches(0, std::nan(""), 2.2);
    check(p.analyses == 20 && p.generates == 60, "unmeasurable estimate: full batches");
    p = PlanBatches(1, 1, -1);
    check(p.analyses == 1 && p.generates == 1, "no budget left: one of each");

    check(QualityBudgetSec(0, 4) == kMaxQualitySec, "budget capped at 2.2 s");
    check(std::abs(QualityBudgetSec(11.5, 2) - 0.5) < 1e-9, "budget shares what is left");
    check(QualityBudgetSec(13, 1) == 0 && QualityBudgetSec(1, 0) == 0, "no budget past the plan");

    check(NextRound(0, 100, 100, 1), "the first round always runs");
    check(NextRound(3, 1.0, 0.5, 2.2) && !NextRound(3, 1.8, 0.5, 2.2), "rounds while one more fits");
    check(!NextRound(kRounds, 0, 0, 100), "at most 9 rounds");
    check(QualityFits(8, 2.2) && !QualityFits(12, 2.2), "a quality starts only when it can end in time");

    check(LoadCyclesPerBatch(5) == 3 && LoadCyclesPerBatch(0.1) == kLoadMaxCycles && LoadCyclesPerBatch(100) == 1 &&
              LoadCyclesPerBatch(0) == 1, "load batch size");
    check(FramesPerSec(300, 1, 2) == 300 && FramesPerSec(150, 0.5, 1.0) == 300, "frames per second");
    check(FramesPerSec(10, 2, 2) == 0 && FramesPerSec(10, 3, 2) == 0, "no time between completions: 0");
}

// What Bench.cpp does with the time, for a GPU whose costs scale with k (k = 1: an RTX 3060 at
// 3840x2160, tests/results/lowend.txt). Setup (device, shaders, picture) counted as 0.6 s.
struct Sim { double total = 0; int lines = 0; };
static Sim simulate(double k) {
    const double aMs[4] = {2.05 * k, 2.86 * k, 7.03 * k, 9.29 * k};   // low .. quality
    const double gMs = 0.68 * k;
    double t = 0.6;
    const double warmStart = t;
    do t += (aMs[2] + 3 * gMs) / 1000; while (t - warmStart < kWarmupSec);
    Sim r;
    double lastSec = 0;
    for (int i = 0; i < 4; ++i) {
        if (i > 0 && !QualityFits(t, lastSec)) break;
        const double q0 = t, budget = QualityBudgetSec(t, 4 - i), a = aMs[i] / 1000, g = gMs / 1000;
        t += 3 * a + 4 * g;   // first frame, two analysed, four generated
        const BatchPlan p = PlanBatches(aMs[i], gMs, budget - (t - q0));
        double last = 0;
        for (int round = 0; NextRound(round, t - q0, last, budget); ++round) {
            last = p.analyses * a + p.generates * g;
            t += last;
        }
        lastSec = t - q0;
        ++r.lines;
    }
    r.total = t;
    return r;
}

static void timePlan() {
    for (double k : {0.05, 0.2, 1.0, 5.0, 10.0, 20.0, 30.0, 45.0}) {
        const Sim r = simulate(k);
        char what[96];
        std::snprintf(what, sizeof what, "GPU cost x%-5.2f: 4 lines in %5.2f s (limit %.0f s)", k, r.total, kLimitSec);
        check(r.lines == 4 && r.total <= kLimitSec - 0.5, what);
    }
    for (double k : {80.0, 150.0, 300.0}) {    // 0.16 to 0.6 s per low-quality analysis: ends in time by itself
        const Sim r = simulate(k);
        char what[96];
        std::snprintf(what, sizeof what, "GPU cost x%-5.0f: %d lines, ends at %5.2f s", k, r.lines, r.total);
        check(r.lines >= 1 && r.total <= kLimitSec, what);
    }
}

int main() {
    commandLine();
    lines();
    numbers();
    timePlan();
    std::printf(failures ? "%d FAILED\n" : "all ok\n", failures);
    return failures ? 1 : 0;
}
