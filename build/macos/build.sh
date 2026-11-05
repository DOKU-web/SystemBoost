#!/usr/bin/env bash
# SystemBoost macOS build: engine (Swift/Metal) + app (Avalonia) -> .app -> Velopack .pkg + updates
# Requirements: Xcode 15+ command line tools, .NET 8 SDK, vpk (dotnet tool install -g vpk --version 0.0.1298).
# Optional signing/notarization (needed so Gatekeeper opens it without warnings):
#   SIGN_IDENTITY="Developer ID Application: ..."  INSTALL_IDENTITY="Developer ID Installer: ..."
#   NOTARY_PROFILE=<notarytool keychain profile>
# Usage: build/macos/build.sh [version]
set -euo pipefail
VERSION="${1:-0.6.0}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/dist/macos"
rm -rf "$OUT"; mkdir -p "$OUT"

# 1) frame generation engine, universal binary (Apple Silicon + Intel)
(cd "$ROOT/native/framegen-mac" && swift build -c release --arch arm64 --arch x86_64)
ENGINE="$ROOT/native/framegen-mac/.build/apple/Products/Release/sb-framegen-mac"

for ARCH in arm64 x64; do
  LABEL=$([ "$ARCH" = arm64 ] && echo "AppleSilicon" || echo "Intel")
  APP="$OUT/$LABEL/SystemBoost.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  # 2) app, self-contained (no .NET install needed)
  dotnet publish "$ROOT/app/SystemBoost.App/SystemBoost.App.csproj" -c Release -r "osx-$ARCH" \
    --self-contained true -p:Version="$VERSION" -p:UseAppHost=true -o "$APP/Contents/MacOS"
  cp "$ENGINE" "$APP/Contents/MacOS/sb-framegen-mac"
  sed "s/__VERSION__/$VERSION/g" "$ROOT/build/macos/Info.plist" > "$APP/Contents/Info.plist"
  chmod +x "$APP/Contents/MacOS/SystemBoost" "$APP/Contents/MacOS/sb-framegen-mac"

  # 3) Velopack: .pkg installer, portable zip and update packages for this architecture.
  #    vpk adds its updater to the bundle, then signs and notarizes when the identities are set.
  CH="osx-$ARCH"
  REL="$OUT/Releases-$LABEL"; mkdir -p "$REL"
  if [ -n "${VPK_REPO_URL:-}" ]; then vpk download github --repoUrl "$VPK_REPO_URL" --token "${VPK_TOKEN:-}" -o "$REL" --channel "$CH" || echo "no previous release: full package only"; fi
  ARGS=(pack -u SystemBoost -v "$VERSION" -p "$APP" --mainExe SystemBoost --packTitle SystemBoost --channel "$CH" -o "$REL")
  [ -f "$ROOT/RELEASE_NOTES.md" ] && ARGS+=(--releaseNotes "$ROOT/RELEASE_NOTES.md")
  if [ -n "${SIGN_IDENTITY:-}" ]; then
    ARGS+=(--signAppIdentity "$SIGN_IDENTITY" --signEntitlements "$ROOT/build/macos/entitlements.plist")
    [ -n "${INSTALL_IDENTITY:-}" ] && ARGS+=(--signInstallIdentity "$INSTALL_IDENTITY")
    [ -n "${NOTARY_PROFILE:-}" ] && ARGS+=(--notaryProfile "$NOTARY_PROFILE")
  else
    codesign --force --deep --sign - "$APP"   # ad-hoc: runs locally, Gatekeeper will warn on other Macs
  fi
  vpk "${ARGS[@]}"

  # 4) fixed names for the download page (releases/latest/download/...)
  mkdir -p "$OUT/download"
  cp "$(ls "$REL"/*-Setup.pkg | head -1)" "$OUT/download/SystemBoost-macOS-$LABEL.pkg"
done
ls -la "$OUT"
