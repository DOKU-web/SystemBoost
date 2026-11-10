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

    // Call at every stats line with the game's rate (never more than the screen can show). True = the
    // output stayed far under it for kCollapseSec. The overlay covers the game, so the screen shows a
    // fraction of the frames the game makes, and no step down repairs a gap like that: the game is
    // shown at once. Recorded runs sat through 6 to 10 s of 5 fps before the steps ran out.
    bool collapsed(double now, double outFps, double gameFps) {
        if (gameFps <= 0 || outFps >= gameFps * kCollapseShare) { collapseSince_ = -1; return false; }
        if (collapseSince_ < 0) { collapseSince_ = now; return false; }
        if (now - collapseSince_ < kCollapseSec) return false;
        collapseSince_ = -1;
        return true;
    }

    // Generation starts (again): what was counted before says nothing about it.
    void settle(double now) {
        stepped(now, kSettleHeavySec);
        collapseSince_ = -1;
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
    static constexpr double kCollapseShare = 0.6;
    static constexpr double kCollapseSec = 0.4;  // two stats lines in a row

    double lastNow_ = -1;
    uint64_t lastSkipped_ = 0;
    double overSince_ = -1;
    double settleUntil_ = 0;
    double behindSince_ = -1;
    double collapseSince_ = -1;
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
    // The rate is known already: the game was shown as it is until now.
    void set(double now, double fps) {
        fps_ = fps;
        until_ = now + kCompareSec;
        reading_ = false;
    }

private:
    static constexpr double kReadSec = 1.0;
    static constexpr double kCompareSec = 15.0;

    double from_ = 0, fps_ = 0, until_ = 0;
    uint64_t frames_ = 0;
    bool reading_ = false;
};

// Generation can only add what the game leaves under the limit: the monitor's refresh rate, or the
// output rate the user chose. A game within kRestShare of it gains next to nothing (Pacer::kMinGain)
// and pays for the capture, the overlay and the GPU time: a game at 135 fps on a 144 Hz monitor can
// only lose. Such a game is shown as it is, and generation starts when its rate falls clear of the limit.
class NearLimit {
public:
    // The game's rate read with nothing generated, just before generation would start. True = rest.
    bool judgeAlone(double aloneFps, double limit) {
        since_ = -1;
        resting_ = limit > 0 && aloneFps >= limit * kRestShare;
        return resting_;
    }
    // Call at every stats line with the source rate. True = the state changed (see resting()).
    bool update(double now, double srcFps, double limit) {
        const bool crossed = resting_ ? srcFps < limit * kResumeShare : srcFps >= limit * kRestShare;
        if (!crossed || srcFps <= 0 || limit <= 0) { since_ = -1; return false; }
        if (since_ < 0) since_ = now;
        if (now - since_ < (resting_ ? kResumeSec : kRestSec)) return false;
        resting_ = !resting_;
        since_ = -1;
        return true;
    }
    bool resting() const { return resting_; }

private:
    static constexpr double kRestShare = 0.80;    // 1 / Pacer::kMinGain
    static constexpr double kResumeShare = 0.72;  // apart, so a game hovering at one line does not flip
    static constexpr double kRestSec = 3.0;       // while generating the source rate is lower than the game alone
    static constexpr double kResumeSec = 2.0;

    bool resting_ = false;
    double since_ = -1;
};

}  // namespace sbfg
