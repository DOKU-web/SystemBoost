# PC 맞춤 추천 — contract between the parts (2026-11-18)

Owner decisions (2026-11-18): 1) measure everything: read the specs AND run short benchmarks; 2) recommend everything:
frame generation, boost items, overclock / power limit; 3) show the recommendations, one button applies them all at once;
4) Ultra plan and up (`PremiumFeature.PcCheck`, already in License.cs UltraOnly).

Base commit: feat/pc-check (on feat/accounts 5226d38 = the 0.8.18 release candidate). Target: app 0.8.19.

## Parts, branches, files

| Part | Branch / worktree | Files it owns (touch nothing else; report overlaps) |
|---|---|---|
| native | feat/pc-native, C:\dev\SystemBoost-wt-pc-native | native/framegen/** (new Bench.cpp/.h, main.cpp, Settings.h, CMakeLists.txt, README_FRAMEGEN.md, tests) |
| advisor | feat/pc-advisor, C:\dev\SystemBoost-wt-pc-advisor | Services/PcCheck/Pure/PcAdvisor.cs, Pure/CpuBenchmark.cs, (new Pure/*.cs of its own), Services/PcCheck/PcSpecReader.cs, app/SystemBoost.PcCheck.Tests/AdvisorTests.cs, docs/pc-check/lang-advisor.json |
| runner | feat/pc-runner, C:\dev\SystemBoost-wt-pc-runner | Services/PcCheck/PcCheckRunner.cs, new Pure/FgBenchProtocol.cs, Pure/PcCheckStore.cs (or similar pure files), new Services/WindowsBoostBackend.PcCheck.cs (partial), ViewModels/FrameGenViewModel.cs (saved choice only), RunnerTests.cs, docs/pc-check/lang-runner.json |
| ui | feat/pc-ui, C:\dev\SystemBoost-wt-pc-ui | new Pages/PcCheckPage.cs, MainWindow.axaml.cs (one nav line), Pages/Pages.cs (dashboard card), Pages/PremiumPage.cs (plan table row), docs/pc-check/lang-ui.json |

All paths under app/SystemBoost.App unless they start with app/ or docs/ or native/. Pure/PcCheckModels.cs is shared and
frozen: if a part needs a change there, it reports it instead of editing (the lead changes it).
Code against the stubs: PcAdvisor / CpuBenchmark / PcSpecReader (advisor) and PcCheckRunner (runner) keep their public
signatures exactly; a part may add members, never rename or remove.

## Rules for every part (from the owner's standing instructions)

- No code into a game process, no reading game memory, no game files. The check refuses to start while a game runs.
- Every PC setting change goes through what already backs up and restores: `IBoostBackend.ApplyAsync/RevertAsync`,
  the gpu.oc path (`SetGpuChoice` + ApplyAsync("gpu.oc"), the helper's confirm / auto-restore). Nothing new writes the
  registry, power plans or the driver directly.
- Overclock / power limit: only after the overclocking notice (`OcConsent.EnsureAsync`; for applying a recommendation
  `fromPreset: true`, which always asks). Desktop NVIDIA only (AMD driver features are not in this build: AmdBuild.DriverFeatures
  false; Intel none; laptops: no power-limit recommendation). Clock offsets are NOT recommended (a one-minute test
  cannot show a stable offset): only the power limit, within the card's own range, measured on and off.
- No exaggeration. Every recommendation says where it comes from: "측정" with the numbers, or "사양 기준". Nothing
  claims a gain that was not measured. Unmeasured effects say "효과는 게임마다 달라요".
- Every new user-visible Korean string has an English entry. Do NOT edit Assets/lang.en.json (all parts would collide):
  put `{"<Korean>": "<English>"}` pairs in your docs/pc-check/lang-<part>.json (UTF-8, no BOM); `{0}` slots as Lang.cs.
  The lead merges them. Design stays: black background, orange accent #FF7A1A, the Ui.* helpers.
- Results stay on this PC (data\pc-check.json); nothing is sent anywhere. No telemetry.
- Don't run any benchmark, GPU load, the app's GUI or anything that changes the PC on this machine: the owner is
  playing a game. Build and run unit tests only (below-normal priority). Say what stayed unverified.
- No secrets in code or commits. Before each commit: `powershell -NoProfile -ExecutionPolicy Bypass -File
  C:\Users\eunin\AppData\Local\Temp\claude\e511019207cd9ca4\a7f14f69-0142-4125-8048-317c583da6bf\scratchpad\scan_cached.ps1 -Repo <worktree>`
  (must print "secret scan clean"). Commit on your branch only; never push, tag, merge into other branches or touch main.
  Commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; write the message to a file without
  BOM and use `git commit -F`. Unset GITHUB_TOKEN / GH_TOKEN for git.
- Never kill SystemBoost or games by name. Don't create C:\tmp.

## Native: `sb_framegen.exe --bench` (protocol)

```
sb_framegen.exe --bench --bench-width W --bench-height H [--gen-gpu same|<value of --list-gpus>] [--parent-pid P] [--shader-dir D]
sb_framegen.exe --bench-load S --bench-width W --bench-height H [--gen-gpu ...] [--parent-pid P] [--shader-dir D]
```
- No window, no capture, no overlay, no other process touched. Synthetic moving pictures (like tests/gen_bench.cpp),
  the real analysis + interpolation passes of FrameGenGpu at W×H.
- `--bench`: for each quality low, performance, balanced, quality, one stdout JSON line
  `{"type":"bench","quality":"balanced","width":2560,"height":1440,"analyzeMs":1.234,"genMs":0.456,"gpu":"<adapter name>"}`
  (median over batches, GPU-waited batches like gen_bench), then `{"type":"bench_done"}`, exit 0. Whole run ≤ 15 s.
- `--bench-load S` (1..60): keeps the GPU busy with the balanced passes for S seconds and prints once a second
  `{"type":"bench_load","sec":1,"framesPerSec":812.5,"gpu":"<adapter name>"}`, then `{"type":"bench_done"}`.
  framesPerSec = generated frames per second, GPU-bound (used to compare power limits on the same load).
- Failure: `{"type":"error","message":"<English text>"}` and exit code ≠ 0. Ends when --parent-pid ends.
- Lines are UTF-8, one JSON object per line, invariant culture numbers.

## Advisor: rules (PcAdvisor.Recommend, pure)

Order on the page: tips that need the user (display refresh, RAM), frame generation, boost items, GPU power.
- Frame generation (Ultra has auto and 2-8x): from the bench at the display resolution. For output R = display refresh
  and multiplier m in {2,3,4,6,8}: source fps R/m must stay ≥ 30 (below that the added delay grows: FrameGenViewModel
  notes 113 ms under 20 fps); GPU cost per second = analyzeMs·R/m + genMs·R·(1−1/m) must stay ≤ 200 ms (20 % of the
  GPU). Recommend Mode "auto", the largest MaxMul that fits, the best quality that fits (quality > balanced >
  performance > low). Nothing fits: recommend leaving it off, with the measured cost. Integrated-only GPU: say so.
  Reason carries the measured ms and the resolution.
- Boost items (BoostCatalog ids; Applicable only when not already on in PcNow.BoostsOn; only ids that exist on Windows):
  sys.gamemode (Game Mode off → on), sys.dvr (Xbox background recording on → off), sys.focus, gpu.highperf (laptop
  or both an integrated and a discrete GPU), cpu.ecores (hybrid CPU), ram.standby (RAM ≤ 16 GB), cpu.power only when
  the measured high-performance-plan gain is ≥ 3 % and ≥ 2× noise (say "벤치마크 기준", cpu.power acts only during
  games), boost.profiles as a pointer to 게임별 측정 (not selected by default). Each with its reason.
- GPU power (Kind Gpu, PowerW): only with a GpuPowerTest without Fault, gain ≥ 3 % and ≥ 2× noise, MaxTempC under
  80. SelectedByDefault FALSE (overclocking is opt-in; the notice is asked on apply). Text names the measured fps.
- Tips (Applicable false): display set below its max refresh at this resolution; RAM running below its rated speed
  (XMP/EXPO in the BIOS, the app does not change it); one RAM stick (dual channel); laptop on battery.
- Every Title/Current/Proposed/Reason Korean, with an English pair in lang-advisor.json (templates with {0}).

## Runner (PcCheckRunner, Windows)

- CannotStart: not `License.Allowed(PremiumFeature.PcCheck)` → "PC 맞춤 추천은 울트라 기능이에요. 왼쪽 '★ 플랜'에서 열 수 있어요.";
  a game running (the backend's game detection) → "게임을 끈 뒤에 검사해 주세요"; already running.
- RunAsync: PcSpecReader.ReadAsync → CpuBenchmark.RunAsync (~2 s × 3 per side) → if a high-performance plan exists
  and is not active: the multi-thread run again on it (switch through the backend's own power-plan code with its
  backup, always switched back in finally) → sb_framegen --bench at the display resolution → if testPower and a
  desktop NVIDIA card with PowerMaxW > PowerDefaultW: OcConsent.EnsureAsync(owner, gpu, fromPreset: false), the
  admin helper, --bench-load at the default limit, then at PowerMaxW (oc.set power only, clocks 0, temp limit as
  set), then put back (oc.restore), watching temperature and the helper's faults; a fault ends the test and is
  reported → PcAdvisor.Recommend → save data\pc-check.json → return. Progress lines in Korean. Cancel restores everything.
- ApplyAsync: GPU items first ask OcConsent.EnsureAsync(..., fromPreset: true) (declined → GPU items skipped, the rest
  applied); boost items via backend.ApplyAsync(id) when not on; frame generation via a new FrameGenViewModel method
  that sets Mode/MaxMul/Quality and saves the choice (the page starts from it next time; the page's 기본값 still goes
  back to the plan default and forgets the saved choice); GPU via SetGpuChoice(PowerW, 0, 0) + ApplyAsync("gpu.oc").
  Writes what was changed (and what it was before) to data\pc-check-applied.json before changing anything.
- UndoAsync: boost items that were off before → RevertAsync; frame generation back to the previous choice; gpu.oc
  back off and the previous GpuChoice. Clears the record.

## UI (PcCheckPage)

- Nav "✦   맞춤 추천" right after 대시보드 (key "pccheck"), and a dashboard card that opens it.
- Below Ultra: the explanation, the 울트라 badge and the button answers with the plan line (like 게임별 측정).
- Start: checkbox "전력 제한도 시험하기 (NVIDIA 데스크톱, 관리자 권한과 오버클럭 안내 동의가 필요해요)", button "검사 시작",
  the progress line, a cancel button. Says it takes about a minute and that games must be closed.
- Result: specs summary, then each recommendation: checkbox (SelectedByDefault; tips have none), title, current →
  proposed, reason. "선택한 추천 한 번에 적용" → per-item results; when a GPU item was applied, the '유지' countdown and
  button (backend.GpuConfirmLeft / KeepGpuAsync, like the GPU page). "추천 적용 되돌리기" when CanUndo. The last report is
  shown when the page opens (with its date).
- Plan table (PremiumPage): a row "PC 맞춤 추천 (사양 측정 · 벤치마크 · 한 번에 적용)" for 울트라 and 산업.

## Build and tests

- App: `dotnet build app/SystemBoost.App -c Release` (VS dev env not needed for C#). New tests:
  `dotnet run --project app/SystemBoost.PcCheck.Tests -c Release`. Existing: app/SystemBoost.Account.Tests, app/SystemBoost.Gpu.Tests.
- Native: see build/windows/build.ps1 for how sb_framegen is built; VS at C:\Program (`call "C:\Program\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64`),
  CMake at C:\Program\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin.
- Below-normal priority for builds (e.g. `Start-Process ... -PassThru` then `.PriorityClass = 'BelowNormal'`, or `start /belownormal /wait`).
