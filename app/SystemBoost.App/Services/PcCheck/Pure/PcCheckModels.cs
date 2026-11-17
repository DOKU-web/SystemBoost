// PC 맞춤 추천 (Ultra and Biz): what the check reads, what it measures and what it recommends.
// Pure data, no Windows or Avalonia calls: app/SystemBoost.PcCheck.Tests links every file of this Pure folder.
// The contract between the parts is docs/pc-check/CONTRACT.md. All of it stays on this PC (data\pc-check.json).
using System;
using System.Collections.Generic;

namespace SystemBoost.App.Services.PcCheck;

/// Vendor: "nvidia", "amd", "intel" or "other". EngineValue: the --gen-gpu value of sb_framegen --list-gpus ("" if unknown).
public sealed record GpuSpec(string Name, string Vendor, bool Integrated, long VramMB, string EngineValue = "");

/// The primary display now, and the highest refresh rate it offers at this resolution.
public sealed record DisplaySpec(int Width, int Height, double RefreshHz, double MaxRefreshHz);

/// Windows settings that boost items change. Null: could not be read.
public sealed record WindowsState(bool? Hags, bool HagsSupported, bool? GameMode, bool? XboxDvr,
                                  bool HighPerfPlanActive, bool HighPerfPlanExists);

/// ECores 0 when the CPU is not hybrid. RAM speeds in MT/s, 0 when unknown. RamSticks 0 when unknown.
public sealed record PcSpecs(string CpuName, int Cores, int Threads, int PCores, int ECores,
                             long RamMB, int RamConfiguredMTs, int RamRatedMTs, int RamSticks,
                             IReadOnlyList<GpuSpec> Gpus, DisplaySpec Display,
                             bool Laptop, bool OnBattery, WindowsState Windows);

/// Scores are work units per second (higher is better). NoisePercent: spread between repeats.
/// HighPlan*: the same multi-thread run on the high-performance power plan; null when not measured.
public sealed record CpuBench(double SingleScore, double MultiScore, double NoisePercent,
                              double? HighPlanMultiScore = null, double? HighPlanNoisePercent = null);

/// GPU time of frame generation at the display's resolution, measured by sb_framegen --bench (no game, no window).
/// AnalyzeMs per game frame, GenMs per generated frame. Quality: low, performance, balanced or quality.
public sealed record FgBench(string Quality, int Width, int Height, double AnalyzeMs, double GenMs, string Gpu = "");

/// The power-limit test on a desktop NVIDIA card: the same GPU load at the default and at the tested limit.
/// Fault: null, or why it stopped ("driver", "temp:<C>", "helper", ...); the card is put back either way.
public sealed record GpuPowerTest(string Gpu, int DefaultW, int TestedW, double DefaultFps, double TestedFps,
                                  double NoisePercent, int MaxTempC, string? Fault = null);

public sealed record BenchResults(CpuBench? Cpu, IReadOnlyList<FgBench> FrameGen, GpuPowerTest? Power,
                                  string? CpuError = null, string? FgError = null, string? PowerError = null);

/// Frame generation settings of the FrameGen page: Mode "auto", "x2", "x3", "x4", "x6", "x8" or "off".
public sealed record FgChoice(string Mode, int MaxMul, string Quality);

public enum RecKind { Boost, FrameGen, Gpu, Tip }

/// One line of the result. Texts are Korean (each has an entry in lang.en.json) and say whether they come from a
/// measurement ("측정") or from the specs ("사양 기준"). Tips are shown, never applied (Applicable false).
/// What applying does: BoostFeatureId (a BoostCatalog id), FrameGen, or PowerW (NVIDIA power limit, clocks untouched).
public sealed record Recommendation(string Id, RecKind Kind, string Title, string Current, string Proposed, string Reason,
                                    bool Applicable, bool SelectedByDefault,
                                    string? BoostFeatureId = null, FgChoice? FrameGen = null, int? PowerW = null);

/// What the app knows now, besides the specs: boost items that are on, and the frame generation choice.
public sealed record PcNow(IReadOnlyCollection<string> BoostsOn, FgChoice FrameGen);

public sealed record PcCheckReport(DateTime At, string AppVersion, PcSpecs Specs, BenchResults Bench,
                                   IReadOnlyList<Recommendation> Recommendations);

/// One line after "한 번에 적용".
public sealed record ApplyResult(string Id, bool Ok, string Text);
