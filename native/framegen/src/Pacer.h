// SystemBoost FrameGen v0.6 - frame pacing & multiplier selection.
// Header-only, no Windows dependencies (unit-tested on Linux: tests/pacer_sim.cpp).
//
// Display-clock model
// -------------------
// Output frames are produced on an even output clock (every vblank divider in
// Auto mode, or every I/N in fixed xN mode). For an output frame that will be
// on screen at time V, the content shown is the game state at c = V - D, where
// D is one constant delay for every frame. t = (c - tA) / (tB - tA) is computed
// from real timestamps, so jitter in when source frames arrive does not turn
// into uneven motion on screen.
//
// D cannot be smaller than one source interval + processing time (frame B must
// exist before anything between A and B can be shown); that is the latency
// cost of interpolation. D glides toward a long recent interval, so ordinary
// jitter does not leave the output waiting for a frame that has not arrived.
//
// Capture timestamps land on vblanks: 40 fps on 144 Hz reads as a mix of 20.8 and
// 27.8 ms. Their median is one of those two, so the rate has to come from the
// mean, whose sum still spans the true time.
#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace sbfg {

enum class MulMode : int { Off = 1, X2 = 2, X3 = 3, X4 = 4, Auto = 0 };

struct PacerConfig {
    MulMode mode = MulMode::Auto;
    double refreshHz = 60.0;     // display refresh (DwmGetCompositionTimingInfo)
    int maxMul = 4;              // Auto: output fps <= source fps * maxMul (see kDividerSlack)
    double vblankPhase = 0.0;    // time of any known vblank (seconds)
    double switchHoldSec = 1.0;  // Auto: hysteresis for divider changes
    double marginSec = 0.0005;   // extra safety added to D
};

struct OutputSlot {
    double presentAt = 0;  // call Present at/after this time
    double displayAt = 0;  // expected scan-out time (used for t)
};

class Pacer {
public:
    explicit Pacer(const PacerConfig& c = {}) : cfg_(c) { reset(); }

    void setConfig(const PacerConfig& c) { cfg_ = c; outputCap_ = 0; reset(); }
    // Refresh rate / vblank phase updates (no reset; called periodically).
    void setDisplay(double refreshHz, double vblankPhase) {
        cfg_.refreshHz = refreshHz;
        cfg_.vblankPhase = vblankPhase;
    }
    const PacerConfig& config() const { return cfg_; }

    void reset() {
        count_ = pos_ = 0;
        lastArrival_ = -1;
        tA_ = tB_ = -1;
        nextDisplay_ = -1;
        divider_ = 1;
        candidate_ = 0;
        D_ = -1;
        serial_ = 0;
        lastPos_ = -1;
    }

    // A frame reached the capture, used or not; `frames` = the game's frames it stands for.
    // The rate comes from here: counting only the frames that get used would read a GPU
    // that cannot keep up as a slow game, and the output would slow down to match it.
    void onArrival(double time, int frames = 1) {
        if (lastArrival_ > 0) pushInterval((time - lastArrival_) / frames);
        lastArrival_ = time;
    }

    // New source frame in use at time `now`; procSec = GPU time per generated frame.
    void onSource(double now, double procSec) {
        tA_ = tB_;
        tB_ = now;
        ++serial_;
        if (procSec > 0) proc_ = proc_ * 0.8 + procSec * 0.2;
        // D: newest frame must be available when its content time is shown
        const double target = percentile(0.8) + proc_ + cfg_.marginSec;
        D_ = D_ < 0 ? target : D_ + (target - D_) * 0.1;  // glide, no sudden jumps
        updateDivider(now);
        if (nextDisplay_ < 0) nextDisplay_ = snapUp(now + proc_ + vsync());
    }

    bool active() const { return tA_ > 0; }

    // A new frame turns the pair (A,B) into (B,new). With D longer than the last
    // interval the next slot may still belong between A and B, so the new frame
    // waits until the output clock has passed B.
    bool wantsSource() const { return !active() || nextDisplay_ - D_ >= tB_; }

    // Next output slot (does not advance).
    OutputSlot next() const {
        // present early enough to finish generation before that vblank
        return {nextDisplay_ - proc_ - 0.0005, nextDisplay_};
    }

    // Interpolation position for a frame shown at displayAt. Returns 0..1
    // relative to the current pair (A,B). Records lateness.
    float tFor(double displayAt) {
        if (!active()) return 1.f;
        const double c = displayAt - D_;
        const double I = tB_ - tA_;
        double t = I > 0 ? (c - tA_) / I : 1.0;
        if (t > 1.0) {  // next frame not here yet (game hitch): hold B
            ++stats.late;
            t = 1.0;
        }
        if (t < 0.0) t = 0.0;  // (source arrived early) hold A
        return float(t);
    }

    // True if the image for t is the one already on screen: t=1 held on a late
    // pair, or t=0 on a new pair right after t=1 on the previous one. Call once per slot.
    bool repeats(float t) {
        const double pos = double(serial_) + t;
        const bool same = lastPos_ >= 0 && std::abs(pos - lastPos_) < 1e-3;
        lastPos_ = pos;
        return same;
    }

    // The GPU cannot hold this output rate: lower it by about a quarter. Auto lands on the next
    // vblank divider. False when the output is already close to the source rate.
    bool slowDown() {
        const double out = outputFps();
        if (out <= sourceFps() * kMinGain) return false;
        outputCap_ = out * 0.75;
        return true;
    }
    double outputCap() const { return outputCap_; }

    // Move the output clock to the next slot.
    void advance(double now) {
        const double step = outputStep();
        nextDisplay_ += step;
        // if we fell behind (hitch, alt-tab), resync instead of bursting frames
        if (nextDisplay_ < now + proc_) {
            ++stats.skipped;
            nextDisplay_ = snapUp(now + proc_ + 0.0005);
        }
        if (cfg_.mode == MulMode::Auto) nextDisplay_ = snapNear(nextDisplay_);
    }

    // Mean of recent intervals. A hitch is left out so that one stall does not
    // cut the output rate for the next second.
    double interval() const {
        if (count_ == 0) return 1.0 / 30.0;
        const double limit = percentile(0.5) * kHitchFactor;
        double sum = 0;
        int n = 0;
        for (int i = 0; i < count_; ++i)
            if (hist_[i] <= limit) { sum += hist_[i]; ++n; }
        return sum / n;
    }
    double percentile(double q) const {
        if (count_ == 0) return 1.0 / 30.0;
        std::array<double, kHist> tmp{};
        std::copy(hist_.begin(), hist_.begin() + count_, tmp.begin());
        const int k = std::clamp(int(q * (count_ - 1) + 0.5), 0, count_ - 1);
        std::nth_element(tmp.begin(), tmp.begin() + k, tmp.begin() + count_);
        return tmp[k];
    }
    double sourceFps() const { return 1.0 / interval(); }
    double outputFps() const { return 1.0 / outputStep(); }
    double effectiveMultiplier() const { return outputFps() / sourceFps(); }
    double delayMs() const { return D_ * 1000.0; }                    // content -> screen
    double addedLatencyMs() const { return (D_ - proc_) * 1000.0; }  // vs. showing B directly
    int divider() const { return divider_; }
    double vsync() const { return 1.0 / std::max(1.0, cfg_.refreshHz); }

    struct Stats {
        uint64_t late = 0;      // output needed a frame that had not arrived yet
        uint64_t skipped = 0;   // output clock resynced after a stall
        uint64_t switches = 0;  // Auto divider changes
    } stats;

private:
    static constexpr int kHist = 30;
    static constexpr double kHitchFactor = 4.0;
    // A slower divider cuts the output rate in half, so a game hovering at the limit would
    // flip between 120 and 60 fps. It stays until the source is clearly below the limit.
    static constexpr double kDividerSlack = 1.25;
    static constexpr double kMinGain = 1.25;    // less output over the source than this adds little

    double outputStep() const {
        switch (cfg_.mode) {
        case MulMode::Auto: return divider_ * vsync();
        case MulMode::Off: return interval();
        // the display cannot show more than one image per vblank
        default: return std::max({interval() / double(int(cfg_.mode)), vsync(), outputCap_ > 0 ? 1.0 / outputCap_ : 0.0});
        }
    }

    void pushInterval(double d) {
        if (d <= 0 || d > 0.5) return;  // ignore pauses (alt-tab, loading)
        hist_[pos_] = d;
        pos_ = (pos_ + 1) % kHist;
        count_ = std::min(count_ + 1, kHist);
    }

    double snapUp(double t) const {
        const double p = vsync();
        return cfg_.vblankPhase + std::ceil((t - cfg_.vblankPhase) / p) * p;
    }
    double snapNear(double t) const {
        const double p = vsync();
        return cfg_.vblankPhase + std::round((t - cfg_.vblankPhase) / p) * p;
    }

    // Auto: output every `divider` vblanks so that output <= source * maxMul.
    void updateDivider(double now) {
        if (cfg_.mode != MulMode::Auto) return;
        const double maxOut = sourceFps() * std::max(1, cfg_.maxMul);
        int want = std::max(1, int(std::ceil(cfg_.refreshHz / maxOut - 0.05)));
        if (want > divider_ && cfg_.refreshHz / divider_ <= maxOut * kDividerSlack) want = divider_;
        if (outputCap_ > 0) want = std::max(want, int(std::ceil(cfg_.refreshHz / outputCap_ - 0.05)));
        if (want == divider_) { candidate_ = 0; return; }
        // hysteresis: only switch if the new divider is needed for a while
        if (candidate_ != want) { candidate_ = want; candidateSince_ = now; return; }
        if (now - candidateSince_ >= cfg_.switchHoldSec) {
            divider_ = want;
            candidate_ = 0;
            ++stats.switches;
        }
    }

    PacerConfig cfg_;
    std::array<double, kHist> hist_{};
    int pos_ = 0, count_ = 0;
    double lastArrival_ = -1;
    double tA_ = -1, tB_ = -1;
    double proc_ = 0.002;
    double D_ = -1;
    double nextDisplay_ = -1;
    double outputCap_ = 0;   // fps, 0 = none (slowDown)
    int divider_ = 1;
    int candidate_ = 0;
    double candidateSince_ = 0;
    uint64_t serial_ = 0;   // source frames seen; serial_ + t identifies the image shown
    double lastPos_ = -1;
};

}  // namespace sbfg
