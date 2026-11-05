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
        if (now - overSince_ < kHoldSec) return false;
        overSince_ = -1;
        settleUntil_ = now + kSettleSec;
        return true;
    }

private:
    static constexpr double kMissPerSec = 2.0;   // at 500 ms stats lines: one miss in every line
    static constexpr double kHoldSec = 3.0;
    static constexpr double kSettleSec = 3.0;    // let the last step show its effect

    double lastNow_ = -1;
    uint64_t lastSkipped_ = 0;
    double overSince_ = -1;
    double settleUntil_ = 0;
};

}  // namespace sbfg
