// SystemBoost FrameGen - notices when the GPU cannot keep up.
// Header-only, no Windows dependencies (tests/fit_test.cpp).
//
// Missed output slots are the signal. GPU timings alone say little: a card under light
// load lowers its clocks, and the same analysis read about 5 ms at any window size on an
// RTX 3060, close to a whole interval of a 144 fps game with nothing wrong.
#pragma once
#include <cstdint>

namespace sbfg {

class GpuFit {
public:
    // Call at every stats line. True = slots missed for kHoldSec: time to step down.
    bool update(double now, uint64_t skipped) {
        const double sec = lastNow_ < 0 ? 0 : now - lastNow_;
        const uint64_t missed = skipped - lastSkipped_;
        lastNow_ = now;
        lastSkipped_ = skipped;
        if (sec <= 0) return false;
        if (missed / sec < kMissPerSec || now < settleUntil_) { overSince_ = -1; return false; }
        if (overSince_ < 0) overSince_ = now;
        // far behind: a card with little room should not sit through every step at full length
        const bool heavy = missed / sec >= kHeavyPerSec;
        if (now - overSince_ < (heavy ? kHoldHeavySec : kHoldSec)) return false;
        stepped(now, heavy ? kSettleHeavySec : kSettleSec);
        return true;
    }

    // Call at every stats line. True = the output stayed under floorFps for kBehindSec. The overlay
    // covers the game, so that many fewer frames are what gets seen: take the biggest step left at
    // once, and stop generating only when none is left.
    bool behind(double now, double outFps, double floorFps) {
        if (outFps >= floorFps || now < settleUntil_) { behindSince_ = -1; return false; }
        if (behindSince_ < 0) behindSince_ = now;
        if (now - behindSince_ < kBehindSec) return false;
        stepped(now, kSettleHeavySec);
        return true;
    }

private:
    void stepped(double now, double settle) {
        overSince_ = -1;
        behindSince_ = -1;
        settleUntil_ = now + settle;
    }

    static constexpr double kMissPerSec = 2.0;   // at 500 ms stats lines: one miss in every line
    static constexpr double kHoldSec = 3.0;
    static constexpr double kSettleSec = 3.0;    // let the last step show its effect
    static constexpr double kHeavyPerSec = 15.0; // one output slot in ten at 144 Hz
    static constexpr double kHoldHeavySec = 1.5;
    static constexpr double kSettleHeavySec = 1.0;
    static constexpr double kBehindSec = 2.5;

    double lastNow_ = -1;
    uint64_t lastSkipped_ = 0;
    double overSince_ = -1;
    double settleUntil_ = 0;
    double behindSince_ = -1;
};

// How fast the game runs with nothing taken from it, read just before generation starts. A GPU with
// no room gives up game frames to generation, and the source rate alone would hide that. Compared
// for a short while only: later scenes run at other rates anyway.
class GameAlone {
public:
    void start(double now, uint64_t srcFrames) {
        from_ = now;
        frames_ = srcFrames;
        reading_ = true;
        until_ = 0;
    }
    // True while the rate is being read: generation waits.
    bool reading(double now, uint64_t srcFrames) {
        if (!reading_) return false;
        if (now - from_ < kReadSec) return true;
        fps_ = double(srcFrames - frames_) / (now - from_);
        until_ = now + kCompareSec;
        reading_ = false;
        return false;
    }
    // 0 = nothing to compare with
    double fps(double now) const { return now < until_ ? fps_ : 0; }

private:
    static constexpr double kReadSec = 1.0;
    static constexpr double kCompareSec = 15.0;

    double from_ = 0, fps_ = 0, until_ = 0;
    uint64_t frames_ = 0;
    bool reading_ = false;
};

}  // namespace sbfg
