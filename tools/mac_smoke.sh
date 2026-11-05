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
ID=$(python3 - "$OUT/windows.json" <<'EOF'
import json, sys
best = None
for line in open(sys.argv[1]):
    try:
        d = json.loads(line)
    except ValueError:
        continue
    for w in d.get('list', []):
        if w.get('app') == 'Safari' and (best is None or w['w'] * w['h'] > best['w'] * best['h']):
            best = w
if best:
    print(best['id'])
EOF
)
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
    med = lambda k: round(statistics.median(r[k] for r in use), 1)
    print(f"{len(rows)} stats, source {med('srcFps')} output {med('outFps')} x{med('multiplier')} gen {med('genMs')} ms analyze {med('analyzeMs')} ms hz {med('refreshHz')} late {use[-1]['late']} skipped {use[-1]['skipped']} source frames {rows[-1]['srcFrames']}")
else:
    print(f"{len(rows)} stats")
EOF
if [ -n "$ID" ]; then
  for MODE in off x2 auto; do
    osascript -e 'tell application "Safari" to activate' > /dev/null 2>&1; sleep 1
    (sleep 15) | limit 30 "$BIN/sb-framegen-mac" --hwnd "$ID" --mode "$MODE" --quality balanced > "$OUT/engine-$MODE.txt" 2>&1 &
    sleep 9
    screencapture -x "$OUT/screen-engine-$MODE.png" 2>> "$OUT/screen-error.txt"
    wait $!; CODE=$?
    say "engine $MODE: exit $CODE $(python3 "$OUT/stats.py" "$OUT/engine-$MODE.txt")"
  done
else
  say "engine run: skipped, no window id"
fi

mkdir -p "$OUT/crash"
cp "$HOME"/Library/Logs/DiagnosticReports/*SystemBoost* "$HOME"/Library/Logs/DiagnosticReports/*sb-framegen* "$OUT/crash/" 2>/dev/null
say "crash reports: $(ls "$OUT/crash" | wc -l | tr -d ' ')"
[ "$RUNNING" = 1 ]
