"""Multiplier test on the real engine. Windows only; moves windows on screen while it runs.

    python -I tests\\measure_multiplier.py <framegen dir> [--out results\\multiplier.txt]

Build fps_window.cpp and screen_counter.cpp into build\\tests first (see their headers).
Each case runs fps_window.exe at a fixed rate, attaches the engine to it, lets it
settle, then reads three things over the same seconds:
  - src / out: the engine's frame counters
  - shown: the swap chain's count of images that reached the screen
  - screen: distinct images counted by screen_counter.exe, outside the engine
A fixed mode targets min(rate * N, refresh). Auto targets the refresh rate, or the
largest refresh / k that keeps the output at or under rate * max-mul.
"""
import argparse
import json
import math
import subprocess
import sys
import threading
import time
from pathlib import Path

SETTLE_SEC = 3.0
MEASURE_SEC = 8.0
RATES = (30, 40, 60)
MODES = ("x2", "x3", "x4", "auto")
TOLERANCE = 0.05


def reader(pipe, sink):
    for line in pipe:
        try:
            sink.append(json.loads(line))
        except ValueError:
            pass


def start(cmd):
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                         text=True, encoding="utf-8", errors="replace")
    lines = []
    threading.Thread(target=reader, args=(p.stdout, lines), daemon=True).start()
    return p, lines


def wait_for(lines, key, timeout=10.0):
    end = time.time() + timeout
    while time.time() < end:
        for item in lines:
            if key in item:
                return item
        time.sleep(0.02)
    raise TimeoutError(key)


def last_stats(lines):
    return next((x for x in reversed(lines) if x.get("type") == "stats"), None)


def target_fps(mode, rate, refresh, max_mul):
    if mode == "auto":
        k = max(1, math.ceil(refresh / (rate * max_mul) - 0.05))
        return refresh / k
    return min(rate * int(mode[1:]), refresh)


def run_case(tests, engine, mode, rate, extra):
    win, win_lines = start([str(tests / "fps_window.exe"), "--fps", str(rate),
                            "--seconds", str(SETTLE_SEC + MEASURE_SEC + 6)])
    info = wait_for(win_lines, "hwnd")
    eng, eng_lines = start([str(engine), "--hwnd", str(info["hwnd"]), "--mode", mode, "--always-active",
                            "--show-in-capture", "--stats-ms", "250", *extra])
    try:
        time.sleep(SETTLE_SEC)
        first = last_stats(eng_lines)
        if first is None:
            errors = [x for x in eng_lines if x.get("type") == "error"]
            raise RuntimeError(errors or "no stats from engine")
        counter = subprocess.run(
            [str(tests / "screen_counter.exe"), "--x", str(info["clientX"]), "--y", str(info["clientY"]),
             "--w", str(info["width"]), "--h", str(info["height"]), "--seconds", str(MEASURE_SEC)],
            capture_output=True, text=True, timeout=MEASURE_SEC + 10)
        second = last_stats(eng_lines)
    finally:
        try:
            eng.stdin.write("quit\n")
            eng.stdin.flush()
        except OSError:
            pass
        try:
            eng.wait(3)
        except subprocess.TimeoutExpired:
            eng.kill()
        win.kill()
    screen = json.loads(counter.stdout) if counter.stdout.strip() else {}
    return first, second, screen


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("framegen", type=Path)
    ap.add_argument("--out", type=Path)
    ap.add_argument("--label", default="")
    ap.add_argument("--max-mul", type=int, default=4)
    a = ap.parse_args()
    tests = a.framegen / "build" / "tests"
    engine = a.framegen / "build" / "Release" / "sb_framegen.exe"
    extra = ["--max-mul", str(a.max_mul)]

    rows = [f"multiplier {a.label}".rstrip(),
            f"{'rate':>4} {'mode':>4} | {'src':>6} {'out':>7} {'x':>5} {'target':>7} {'err%':>6} | "
            f"{'shown':>7} {'screen':>7} | {'repeat':>6} {'late/s':>6} {'skip/s':>6} | {'p10':>5} {'p50':>5} {'p90':>5} {'est':>6}"]
    failed = 0
    for rate in RATES:
        for mode in MODES:
            f, s, screen = run_case(tests, engine, mode, rate, extra)
            dt = s["clockSec"] - f["clockSec"]
            src = s["srcFrames"] - f["srcFrames"]
            out = s["outFrames"] - f["outFrames"]
            src_fps, out_fps = src / dt, out / dt
            shown = (s["shownFrames"] - f["shownFrames"]) / dt
            target = target_fps(mode, rate, s["refreshHz"], a.max_mul)
            err = (out_fps / target - 1) * 100
            ok = abs(err) <= TOLERANCE * 100
            failed += not ok
            rows.append(f"{rate:>4} {mode:>4} | {src_fps:6.1f} {out_fps:7.1f} {out / max(src, 1):5.2f} {target:7.1f} "
                        f"{err:+6.1f} | {shown:7.1f} {screen.get('fps', 0):7.1f} | "
                        f"{s['repeatFrames'] - f['repeatFrames']:6d} {(s['late'] - f['late']) / dt:6.1f} "
                        f"{(s['skipped'] - f['skipped']) / dt:6.1f} | {s['p10Ms']:5.1f} {s['p50Ms']:5.1f} "
                        f"{s['p90Ms']:5.1f} {s['intervalMs']:6.2f}{'' if ok else '  FAIL'}")
            print(rows[-1], flush=True)
    rows.append(f"refresh {s['refreshHz']:.2f} Hz, {MEASURE_SEC:.0f} s per case, tolerance {TOLERANCE:.0%}, failed {failed}")
    print(rows[-1])
    if a.out:
        a.out.parent.mkdir(parents=True, exist_ok=True)
        with a.out.open("a", encoding="utf-8") as fh:
            fh.write("\n".join(rows) + "\n\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
