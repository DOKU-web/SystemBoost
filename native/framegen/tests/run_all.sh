#!/usr/bin/env bash
# Runs every check that does not need Windows. Results go to tests/results/.
# Needs: python3 numpy scipy pillow, g++, glslang-tools, (optional) mingw-w64,
#        (optional) Vulkan loader + lavapipe (mesa-vulkan-drivers) for GPU parity.
set -euo pipefail
FG="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$FG/tests/results"
WORK="$(mktemp -d)"
mkdir -p "$OUT"

echo "== 1. shader syntax (HLSL -> SPIR-V, glslang)"
mkdir -p "$WORK/spv"
for f in "$FG"/shaders/*.hlsl; do
  n=$(basename "$f" .hlsl)
  glslangValidator -D -V -S comp -e main --hlsl-iomap --shift-UBO-binding 0 --shift-sampler-binding 1 \
    --shift-texture-binding 2 --shift-uav-binding 8 --shift-ssbo-binding 2 -o "$WORK/spv/$n.spv" "$f" >/dev/null
  echo "  ok $n"
done

echo "== 2. frame pacing simulation"
g++ -std=c++20 -O2 -Wall -Wextra -I"$FG/src" "$FG/tests/pacer_sim.cpp" -o "$WORK/pacer_sim"
"$WORK/pacer_sim" | tee "$OUT/pacing.txt"

echo "== 2b. frame meter"
g++ -std=c++20 -O2 -Wall -Wextra -I"$FG/src" "$FG/tests/meter_test.cpp" -o "$WORK/meter_test"
"$WORK/meter_test" | tee "$OUT/meter.txt"

echo "== 3. interpolation quality (reference)"
python3 -I "$FG/tests/measure_quality.py" "$FG" | tail -16 | tee "$OUT/quality.txt"

echo "== 4. upscaler quality (reference)"
python3 -I "$FG/tests/measure_upscale.py" "$FG" | tee "$OUT/upscale.txt"

if pkg-config --exists vulkan 2>/dev/null || [ -f /usr/include/vulkan/vulkan.h ]; then
  echo "== 5. GPU parity (real shaders on Vulkan vs reference)"
  g++ -std=c++20 -O2 "$FG/tests/gpu_parity/vk_parity.cpp" -lvulkan -o "$WORK/vk_parity"
  python3 -I "$FG/tests/gpu_parity/compare.py" "$FG" "$WORK/vk_parity" "$WORK/spv" "$WORK/parity" | tee "$OUT/gpu_parity.txt"
else
  echo "== 5. GPU parity skipped (no Vulkan headers)"
fi

if command -v x86_64-w64-mingw32-g++ >/dev/null; then
  echo "== 6. Windows build check (mingw, DXGI capture path; WGC needs MSVC)"
  (cd "$FG/src" && x86_64-w64-mingw32-g++ -std=c++20 -O2 -Wall -Wextra -Werror -Wno-unknown-pragmas \
     -Wno-cast-function-type -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 \
     main.cpp Gpu.cpp Capture.cpp FrameGenGpu.cpp Overlay.cpp Cursor.cpp -o "$WORK/sb_framegen.exe" -static \
     -ld3d11 -ldxgi -ld3dcompiler_47 -ldcomp -ldwmapi -luser32 -lgdi32 -lshell32 -lole32)
  echo "  ok sb_framegen.exe ($(stat -c %s "$WORK/sb_framegen.exe") bytes)"
fi
rm -rf "$WORK"
echo "done -> $OUT"
