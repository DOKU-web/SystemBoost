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
  lipo -archs "$BIN/$f" >> "$OUT/arch.txt" 2>&1
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
screencapture -x "$OUT/screen.png" 2> "$OUT/screen-error.txt"
say "screenshot: exit $?"

# Screen recording needs a permission a hosted machine may not give; the result is recorded, not required.
open -a Calculator; sleep 3
limit 30 "$BIN/sb-framegen-mac" --list-windows > "$OUT/windows.json" 2>&1
say "engine --list-windows: exit $? $(head -c 160 "$OUT/windows.json")"
ID=$(python3 - "$OUT/windows.json" <<'EOF'
import json, sys
for line in open(sys.argv[1]):
    try:
        d = json.loads(line)
    except ValueError:
        continue
    for w in d.get('list', []):
        if w.get('app') == 'Calculator':
            print(w['id'])
            sys.exit()
EOF
)
if [ -n "$ID" ]; then
  (sleep 12) | limit 30 "$BIN/sb-framegen-mac" --hwnd "$ID" --mode x2 --quality balanced > "$OUT/engine-run.txt" 2>&1
  say "engine run: exit $? $(grep -c '"type":"ready"' "$OUT/engine-run.txt") ready, $(grep -c '"type":"stats"' "$OUT/engine-run.txt") stats"
else
  say "engine run: skipped, no window id"
fi

pkill -x SystemBoost
mkdir -p "$OUT/crash"
cp "$HOME"/Library/Logs/DiagnosticReports/*SystemBoost* "$HOME"/Library/Logs/DiagnosticReports/*sb-framegen* "$OUT/crash/" 2>/dev/null
say "crash reports: $(ls "$OUT/crash" | wc -l | tr -d ' ')"
[ "$RUNNING" = 1 ]
