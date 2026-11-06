#!/usr/bin/env bash
# Installs a Mac package on a real macOS machine and starts it. Run by .github/workflows/mac-smoke.yml.
# The results land in smoke/, and summary.txt says what passed. Only a failed install or start fails the job.
# Usage: tools/mac_smoke.sh <package.pkg>
set -uo pipefail
PKG="$1"
OUT="$PWD/smoke"; mkdir -p "$OUT"
SUMMARY="$OUT/summary.txt"
limit() { perl -e 'alarm shift; exec @ARGV' "$@"; }    # macOS has no timeout command
say() { echo "$1" | tee -a "$SUMMARY"; }

sw_vers > "$OUT/macos.txt"; uname -m >> "$OUT/macos.txt"
say "machine: $(sw_vers -productVersion) $(uname -m)"

pkgutil --check-signature "$PKG" > "$OUT/pkg-signature.txt" 2>&1
spctl -a -vv -t install "$PKG" > "$OUT/gatekeeper.txt" 2>&1
say "gatekeeper: $(head -1 "$OUT/gatekeeper.txt")"

if ! sudo installer -verbose -pkg "$PKG" -target / > "$OUT/install.txt" 2>&1; then
  say "install: FAILED"; exit 1
fi
APP=""
for p in /Applications/SystemBoost.app "$HOME/Applications/SystemBoost.app"; do [ -d "$p" ] && APP="$p"; done
if [ -z "$APP" ]; then say "install: no SystemBoost.app"; exit 1; fi
say "install: $APP"
pkill -x SystemBoost; sleep 2    # the installer may have opened it

BIN="$APP/Contents/MacOS"
ls -la "$BIN" > "$OUT/files.txt"
for f in SystemBoost sb-framegen-mac UpdateMac; do
  [ -f "$BIN/$f" ] || { say "file: $f missing"; continue; }
  codesign -dv "$BIN/$f" > "$OUT/sign-$f.txt" 2>&1
  say "file: $f $(lipo -archs "$BIN/$f" 2>&1) $(grep -m1 '^Signature' "$OUT/sign-$f.txt")"
done
codesign --verify --deep --strict --verbose=2 "$APP" > "$OUT/bundle-verify.txt" 2>&1
say "bundle seal: exit $?"

limit 30 "$BIN/sb-framegen-mac" --list-gpus > "$OUT/gpus.json" 2>&1
say "engine --list-gpus: exit $? $(head -c 200 "$OUT/gpus.json")"

open --stdout "$OUT/app-stdout.txt" --stderr "$OUT/app-stderr.txt" "$APP"
sleep 25
if pgrep -x SystemBoost > "$OUT/app-pid.txt"; then say "app: running after 25 s"; RUNNING=1; else say "app: NOT running after 25 s"; RUNNING=0; fi
screencapture -x "$OUT/screen-first-run.png" 2> "$OUT/screen-error.txt"
say "screenshot: exit $?"

# What System Settings stores when the user turns Screen Recording on for the app.
pkill -x SystemBoost; sleep 2
codesign -d -r- "$APP" 2>&1 | sed -n 's/^.*designated => //p' > "$OUT/requirement.txt"
csreq -r "$OUT/requirement.txt" -b "$OUT/requirement.bin"
HEX=$(xxd -p "$OUT/requirement.bin" | tr -d '\n')
NOW=$(date +%s)
sudo sqlite3 "/Library/Application Support/com.apple.TCC/TCC.db" \
  "INSERT OR REPLACE INTO access VALUES('kTCCServiceScreenCapture','com.dokku.systemboost',0,2,3,1,X'$HEX',NULL,0,'UNUSED',NULL,0,$NOW,NULL,NULL,'UNUSED',$NOW);" \
  > "$OUT/grant.txt" 2>&1
say "permission grant: exit $? ($(cut -c1-40 "$OUT/requirement.txt"))"
sudo killall tccd 2> /dev/null; killall tccd 2> /dev/null; sleep 2
open "$APP"
sleep 20
screencapture -x "$OUT/screen-granted.png" 2>> "$OUT/screen-error.txt"
pkill -x SystemBoost; sleep 2

# A page moving at 30 fps gives the engine real source frames; a still window only delivers two.
# Off runs without the overlay, so it shows whether the overlay is what stops the capture.
cat > "$OUT/moving.html" <<'EOF'
<!doctype html><title>SB moving test</title>
<style>html,body{margin:0;height:100%;background:#111}
#b{position:absolute;top:120px;width:160px;height:160px;background:#ff7a1a}</style><div id=b></div>
<script>let x=0,d=12;setInterval(()=>{x+=d;if(x<0||x>700)d=-d;b.style.left=x+'px'},33)</script>
EOF
open -a Safari "$OUT/moving.html"; sleep 8
osascript -e 'tell application "Safari" to activate' > /dev/null 2>&1; sleep 2
limit 30 "$BIN/sb-framegen-mac" --list-windows > "$OUT/windows.json" 2>&1
say "engine --list-windows: exit $? $(head -c 160 "$OUT/windows.json")"
cat > "$OUT/pick.py" <<'EOF'
import json, sys
best = None
for line in open(sys.argv[1]):
    try:
        d = json.loads(line)
    except ValueError:
        continue
    for w in d.get('list', []):
        if w.get('app') == 'Safari' and sys.argv[2] in w.get('title', '') and (best is None or w['w'] * w['h'] > best['w'] * best['h']):
            best = w
if best:
    print(best['id'])
EOF
ID=$(python3 "$OUT/pick.py" "$OUT/windows.json" "")
cat > "$OUT/stats.py" <<'EOF'
import json, statistics, sys
rows = []
for line in open(sys.argv[1]):
    try:
        r = json.loads(line)
    except ValueError:
        continue
    if r.get('type') == 'stats':
        rows.append(r)
    elif r.get('type') == 'error':
        print('error', r.get('msg'))
use = rows[4:]
if use:
    med = lambda k, rs=use: round(statistics.median(r[k] for r in rs), 1)
    line = f"{len(rows)} stats, source {med('srcFps')} output {med('outFps')} x{med('multiplier')} gen {med('genMs')} ms analyze {med('analyzeMs')} ms hz {med('refreshHz')} late {use[-1]['late']} skipped {use[-1]['skipped']} source frames {rows[-1]['srcFrames']}"
    modes = sorted({r['mode'] for r in use})
    if len(modes) > 1:    # the mode changed during the run
        line += ' | ' + ', '.join(f"mode {m} output {med('outFps', [r for r in use if r['mode'] == m])}" for m in modes)
    print(line)
else:
    print(f"{len(rows)} stats")
EOF
# What is on screen, front first: the overlay of a full-screen window does not reach a screenshot.
cat > "$OUT/onscreen.swift" <<'EOF'
import CoreGraphics
let all = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
for w in all {
    let b = w[kCGWindowBounds as String] as? [String: Any] ?? [:]
    print(w[kCGWindowOwnerName as String] ?? "?", "layer", w[kCGWindowLayer as String] ?? "?", b["Width"] ?? 0, b["Height"] ?? 0)
}
EOF
swiftc -o "$OUT/onscreen" "$OUT/onscreen.swift" > "$OUT/onscreen-build.txt" 2>&1
run_engine() {    # name, window, mode, quality, max-mul, stdin line sent after 5 s, output rate
  osascript -e 'tell application "Safari" to activate' > /dev/null 2>&1; sleep 1
  [ -z "${BEFORE:-}" ] || { osascript -e "$BEFORE" > /dev/null 2>&1; sleep 2; }
  (sleep 5; [ -z "$6" ] || echo "$6"; sleep 10) | limit 30 "$BIN/sb-framegen-mac" --hwnd "$2" --mode "$3" --max-mul "$5" --quality "$4" --target-fps "${7:-0}" > "$OUT/engine-$1.txt" 2>&1 &
  sleep 9
  screencapture -x "$OUT/screen-engine-$1.png" 2>> "$OUT/screen-error.txt"
  "$OUT/onscreen" > "$OUT/onscreen-$1.txt" 2>&1
  wait $!; CODE=$?
  say "engine $1: exit $CODE $(python3 "$OUT/stats.py" "$OUT/engine-$1.txt"), fit lines $(grep -c '"type":"fit"' "$OUT/engine-$1.txt"), not in front $(grep -c 'not in front' "$OUT/engine-$1.txt"), overlay on screen $(grep -c '^sb-framegen-mac' "$OUT/onscreen-$1.txt")"
}
if [ -n "$ID" ]; then
  run_engine off "$ID" off balanced 4 ""
  run_engine x2 "$ID" x2 balanced 4 ""
  run_engine auto "$ID" auto balanced 4 ""
  run_engine x2-low "$ID" x2 low 4 ""
  run_engine target90 "$ID" auto balanced 4 "" 90
  run_engine target-live "$ID" auto balanced 4 "target 100"
  # Another process in front while the window stays on top: a launcher and its stream.
  BEFORE='tell application "Finder" to activate' run_engine other-front "$ID" x2 balanced 4 ""
  # A window of another app over it: nothing runs, and the engine says why.
  BEFORE='tell application "Finder"
    activate
    make new Finder window
  end tell' run_engine covered "$ID" x2 balanced 4 ""
  osascript -e 'tell application "Finder" to close every window' > /dev/null 2>&1
  # A full-screen window has a Space of its own, the way a stream usually does.
  full() { osascript -e "tell application \"System Events\" to tell process \"Safari\" to set value of attribute \"AXFullScreen\" of window 1 to $1"; }
  osascript -e 'tell application "Safari" to activate' > /dev/null 2>&1; sleep 1
  if full true > "$OUT/fullscreen.txt" 2>&1; then
    sleep 5
    "$OUT/onscreen" > "$OUT/onscreen-before-full.txt" 2>&1
    osascript -e 'tell application "Finder" to activate' > /dev/null 2>&1; sleep 3    # back on the desktop's Space
    limit 30 "$BIN/sb-framegen-mac" --list-windows > "$OUT/windows-full.json" 2>&1
    FULL=$(python3 "$OUT/pick.py" "$OUT/windows-full.json" "")
    say "full screen list from another Space: id ${FULL:-none} $(head -c 200 "$OUT/windows-full.json")"
    [ -z "$FULL" ] || run_engine full "$FULL" x2 balanced 4 ""
    osascript -e 'tell application "Safari" to activate' > /dev/null 2>&1; sleep 1
    full false >> "$OUT/fullscreen.txt" 2>&1; sleep 4
  else
    say "full screen: skipped, $(head -c 160 "$OUT/fullscreen.txt")"
  fi
else
  say "engine run: skipped, no window id"
fi

# This screen shows 60 images a second at most, so a 30 fps page reads the same at x2 and x8.
# About 7 fps keeps every multiplier under that limit: x2 14, x4 28, x6 42, x8 56.
sed 's/SB moving test/SB slow test/; s/,33)/,143)/' "$OUT/moving.html" > "$OUT/slow.html"
open -a Safari "$OUT/slow.html"; sleep 6
limit 30 "$BIN/sb-framegen-mac" --list-windows > "$OUT/windows-slow.json" 2>&1
SLOW=$(python3 "$OUT/pick.py" "$OUT/windows-slow.json" "SB slow")
if [ -n "$SLOW" ]; then
  for MODE in x2 x4 x6 x8; do run_engine "slow-$MODE" "$SLOW" "$MODE" balanced 8 ""; done
  run_engine slow-auto4 "$SLOW" auto balanced 4 ""
  run_engine slow-auto8 "$SLOW" auto balanced 8 ""
  run_engine slow-live "$SLOW" x2 balanced 8 "mode x8"
else
  say "slow run: skipped, no window id $(head -c 200 "$OUT/windows-slow.json")"
fi

mkdir -p "$OUT/crash"
cp "$HOME"/Library/Logs/DiagnosticReports/*SystemBoost* "$HOME"/Library/Logs/DiagnosticReports/*sb-framegen* "$OUT/crash/" 2>/dev/null
say "crash reports: $(ls "$OUT/crash" | wc -l | tr -d ' ')"
[ "$RUNNING" = 1 ]
