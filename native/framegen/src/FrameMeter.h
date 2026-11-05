// SystemBoost FrameGen - source and output frame rates measured together.
// Both counters run over the same window on the same clock, so the multiplier
// shown to the user is the real ratio of frames, not two separately averaged rates.
// A 500 ms window holds only 15-30 source frames, so one frame either way moves the
// ratio by 3-7%; rates are therefore reported over the last few windows together.
// Header-only, no Windows dependencies.
#pragma once
#include <cstdint>

namespace sbfg {

class FrameMeter {
public:
    struct Sample {
        double seconds = 0;
        uint64_t src = 0;     // new captured frames
        uint64_t out = 0;     // output frames with a new image
        uint64_t repeat = 0;  // output slots that would have shown the same image again
        double srcFps() const { return seconds > 0 ? double(src) / seconds : 0; }
        double outFps() const { return seconds > 0 ? double(out) / seconds : 0; }
        double multiplier() const { return src > 0 ? double(out) / double(src) : 0; }
    };

    void reset(double now) {
        start_ = now;
        cur_ = {};
        filled_ = next_ = 0;
    }
    void onSource() { ++cur_.src; ++totalSrc_; }
    void onOutput(bool repeat) {
        if (repeat) { ++cur_.repeat; ++totalRepeat_; }
        else { ++cur_.out; ++totalOut_; }
    }
    double elapsed(double now) const { return now - start_; }

    // Ends the window at `now`; returns the sum of the last kWindows windows.
    Sample take(double now) {
        cur_.seconds = now - start_;
        windows_[next_] = cur_;
        next_ = (next_ + 1) % kWindows;
        if (filled_ < kWindows) ++filled_;
        start_ = now;
        cur_ = {};
        Sample sum;
        for (int i = 0; i < filled_; ++i) {
            sum.seconds += windows_[i].seconds;
            sum.src += windows_[i].src;
            sum.out += windows_[i].out;
            sum.repeat += windows_[i].repeat;
        }
        return sum;
    }

    // Running totals, not cleared by reset(): tests take the difference of two readings.
    uint64_t totalSrc() const { return totalSrc_; }
    uint64_t totalOut() const { return totalOut_; }
    uint64_t totalRepeat() const { return totalRepeat_; }

private:
    static constexpr int kWindows = 4;
    double start_ = 0;
    Sample cur_;
    Sample windows_[kWindows];
    int filled_ = 0, next_ = 0;
    uint64_t totalSrc_ = 0, totalOut_ = 0, totalRepeat_ = 0;
};

}  // namespace sbfg
