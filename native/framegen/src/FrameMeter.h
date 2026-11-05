// SystemBoost FrameGen - source and output frame rates measured together.
// Both counters run over the same window on the same clock, so the multiplier
// shown to the user is the real ratio of frames, not two separately averaged rates.
// Every rate is frames / elapsed timestamps over the last second. Nothing depends on how
// many stats lines were sent or read, so a late or lost line cannot move a value.
// The first half second after a (re)start is left out: capture hands over a backlog then.
// Header-only, no Windows dependencies.
#pragma once
#include <cstdint>

namespace sbfg {

class FrameMeter {
public:
    static constexpr double kRateSec = 1.0;
    static constexpr double kSettleSec = 0.5;

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
        settleUntil_ = now + kSettleSec;
        cur_ = {};
        filled_ = next_ = 0;
    }
    // Stops the active-time clock until the next reset().
    void pause(double now) {
        activeSec_ += now - start_;
        start_ = now;
    }
    void onSource(int frames = 1) { cur_.src += frames; totalSrc_ += frames; }
    void onOutput(bool repeat) {
        if (repeat) { ++cur_.repeat; ++totalRepeat_; }
        else { ++cur_.out; ++totalOut_; }
    }
    double elapsed(double now) const { return now - start_; }

    // Ends the window at `now`. True once `rate` covers at least kRateSec.
    bool take(double now, Sample& rate) {
        cur_.seconds = now - start_;
        activeSec_ += cur_.seconds;
        const bool settled = start_ >= settleUntil_;
        start_ = now;
        const Sample done = cur_;
        cur_ = {};
        if (!settled) return false;
        windows_[next_] = done;
        next_ = (next_ + 1) % kWindows;
        if (filled_ < kWindows) ++filled_;
        rate = {};
        for (int i = 1; i <= filled_ && rate.seconds < kRateSec; ++i) {
            const Sample& w = windows_[(next_ - i + kWindows) % kWindows];
            rate.seconds += w.seconds;
            rate.src += w.src;
            rate.out += w.out;
            rate.repeat += w.repeat;
        }
        return rate.seconds >= kRateSec;
    }

    // Running totals, not cleared by reset(): the difference of two readings is exact
    // however many readings were missed in between.
    uint64_t totalSrc() const { return totalSrc_; }
    uint64_t totalOut() const { return totalOut_; }
    uint64_t totalRepeat() const { return totalRepeat_; }
    double activeSec() const { return activeSec_; }

private:
    static constexpr int kWindows = 11;  // enough for kRateSec at the shortest stats interval
    double start_ = 0, settleUntil_ = 0;
    Sample cur_;
    Sample windows_[kWindows];
    int filled_ = 0, next_ = 0;
    uint64_t totalSrc_ = 0, totalOut_ = 0, totalRepeat_ = 0;
    double activeSec_ = 0;
};

}  // namespace sbfg
