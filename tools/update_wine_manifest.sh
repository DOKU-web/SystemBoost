#!/usr/bin/env bash
# Refreshes app/SystemBoost.App/Assets/wine-manifest.json from the GitHub API:
# exact download URLs + SHA-256 digests of the redistributable runtime parts.
# The app downloads these on first use and refuses files whose hash differs.
#   wine : Gcenx/macOS_Wine_builds (LGPL-2.1, unmodified build of Wine)
#   dxvk : Gcenx/DXVK-macOS        (zlib, Direct3D 10/11 -> Vulkan)
# Apple's D3DMetal (Game Porting Toolkit) is NOT listed: users install it
# themselves from developer.apple.com and point the app at it.
# Needs: gh (authenticated) or GITHUB_TOKEN + curl, jq.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/app/SystemBoost.App/Assets/wine-manifest.json"
api() {
  if command -v gh >/dev/null; then gh api "$1"
  else curl -fsSL -H "Accept: application/vnd.github+json" ${GITHUB_TOKEN:+-H "Authorization: Bearer $GITHUB_TOKEN"} "https://api.github.com/$1"; fi
}
pick() {  # repo, jq filter for the asset name
  api "repos/$1/releases/latest" | jq -c --arg repo "$1" "
    {repo: \$repo, tag: .tag_name} + (.assets[] | select($2) | {name, url: .browser_download_url, size, sha256: (.digest // \"\" | sub(\"^sha256:\"; \"\"))})" | head -1
}
WINE=$(pick Gcenx/macOS_Wine_builds '.name | test("^wine-devel-.*-osx64\\.tar\\.xz$")')
DXVK=$(pick Gcenx/DXVK-macOS '.name | test("\\.tar\\.(gz|xz)$")')
for v in "$WINE" "$DXVK"; do
  [ -n "$v" ] || { echo "asset not found" >&2; exit 1; }
  [ "$(echo "$v" | jq -r .sha256)" != "" ] || { echo "no digest from GitHub for $(echo "$v" | jq -r .name)" >&2; exit 1; }
done
mkdir -p "$(dirname "$OUT")"
jq -n --argjson wine "$WINE" --argjson dxvk "$DXVK" \
  '{generated: (now | todate), wine: $wine, dxvk: $dxvk,
    notice: "Wine (LGPL-2.1) and DXVK (zlib) are downloaded unmodified from their GitHub releases; source is available there."}' > "$OUT"
cat "$OUT"
