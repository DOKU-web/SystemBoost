// PC 맞춤 추천: "선택한 추천 한 번에 적용" and "추천 적용 되돌리기" as data. Pure: PcCheckRunner reads the state, asks the
// overclocking notice, runs the steps through the backend and the FrameGen page, and writes the record; this file only
// decides which steps, what the record holds, and what goes back.
//   Apply   boost items that are off, in the page's order; then frame generation; then the GPU power limit last (its
//           '유지' countdown starts with it). Already on: skipped. GPU: skipped without the notice agreed to now, while
//           gpu.oc is on already (the user's own tuning stays), or when the card cannot take it (GpuProblem).
//   Record  pc-check-applied.json, written before anything changes: the boost items turned on here (they were off),
//           frame generation's values and saved choice before, the GPU page's values before. A second apply adds to it
//           and keeps the earliest "before", so one undo goes back to how the PC was before the first.
//   Undo    boost items turned on here and still on go off; frame generation and the GPU values go back only while
//           they still hold what was set here (RestoreRules' rule: a value the user changed since stays theirs).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SystemBoost.App.Services.PcCheck;

/// The GPU page's values (GpuChoice): PowerW 0 is the card's default.
public sealed record GpuValues(int PowerW, int CoreMHz, int MemMHz);

/// What the plan opens on the FrameGen page (License), and the page's defaults for it.
public sealed record FgAllowance(bool AutoAndHigh, bool UltraMultiplier, bool QualityMode, string DefaultMode, int DefaultMaxMul);

public static class FgChoiceRules
{
    public static readonly IReadOnlyList<string> Modes = new[] { "auto", "x2", "x3", "x4", "x6", "x8", "off" };
    public static readonly IReadOnlyList<int> MaxMuls = new[] { 2, 3, 4, 6, 8 };

    /// The choice as far as the plan allows it (FrameGenPage locks the same values): 6x and 8x need the Ultra multiplier,
    /// auto, 3x and 4x the Pro multipliers, "quality" the quality mode. A value the page does not know is its default.
    public static FgChoice Fit(FgChoice c, FgAllowance a)
    {
        int highest = a.UltraMultiplier ? 8 : a.AutoAndHigh ? 4 : 2;
        string mode = Modes.Contains(c.Mode) ? c.Mode : a.DefaultMode;
        if (mode is "x6" or "x8" && !a.UltraMultiplier) mode = a.AutoAndHigh ? "x4" : "x2";
        if (mode is "auto" or "x3" or "x4" && !a.AutoAndHigh) mode = "x2";
        int max = MaxMuls.Contains(c.MaxMul) ? c.MaxMul : a.DefaultMaxMul;
        if (max > highest) max = highest;
        string quality = FgBenchProtocol.Qualities.Contains(c.Quality) ? c.Quality : "balanced";
        if (quality == "quality" && !a.QualityMode) quality = "balanced";
        return new FgChoice(mode, max, quality);
    }

    public static bool Same(FgChoice? a, FgChoice? b) =>
        a != null && b != null && a.Mode == b.Mode && a.MaxMul == b.MaxMul && a.Quality == b.Quality;
}

/// Frame generation before the apply: the page's values, and what fg-choice.json held (null: no file). Applied: what was set.
public sealed record FgUndo(FgChoice Before, FgChoice? BeforeSaved, FgChoice Applied);

/// The GPU page's values before the apply, and the power limit set (clocks 0).
public sealed record GpuUndo(GpuValues Before, int AppliedW);

/// pc-check-applied.json. Boosts: BoostCatalog ids that were off and were turned on here, in the order they were.
public sealed record PcApplyRecord(DateTime At, IReadOnlyList<string> Boosts, FgUndo? FrameGen = null, GpuUndo? Gpu = null)
{
    [JsonIgnore] public bool IsEmpty => (Boosts == null || Boosts.Count == 0) && FrameGen == null && Gpu == null;
}

/// What the PC and the app hold right before the apply. GpuProblem: why the card cannot take a power limit now (null:
/// it can). GpuConsent: the overclocking notice was agreed to for this apply.
public sealed record PcApplyState(IReadOnlyCollection<string> BoostsOn, FgChoice FgNow, FgChoice? FgSaved,
                                  bool GpuTuned, GpuValues GpuChoice, string? GpuProblem, bool GpuConsent);

public enum ApplyStepKind { Boost, FrameGen, Gpu }

/// One change to make. FrameGen: the choice as the plan allows it. PowerW: the power limit in watts (clocks 0).
public sealed record ApplyStep(string RecId, ApplyStepKind Kind, string? BoostId = null, FgChoice? FrameGen = null, int PowerW = 0);

/// Steps in the order to run them; Skipped: a result line for each chosen item that is not run; Record: what to write
/// to pc-check-applied.json before the first step (empty when there are no steps); Order: the chosen ids as the page
/// lists them, for the results.
public sealed record ApplyPlan(IReadOnlyList<ApplyStep> Steps, IReadOnlyList<ApplyResult> Skipped, PcApplyRecord Record,
                               IReadOnlyList<string> Order);

/// What the PC and the app hold when undo is pressed.
public sealed record PcUndoState(IReadOnlyCollection<string> BoostsOn, FgChoice FgNow, bool GpuTuned, GpuValues GpuChoice);

/// RevertBoosts: newest first. FrameGen / Gpu: put back (null: nothing, or changed since). TurnGpuOff: gpu.oc first.
/// Kept: a line for each value that changed since and stays.
public sealed record UndoPlan(IReadOnlyList<string> RevertBoosts, FgUndo? FrameGen, GpuUndo? Gpu, bool TurnGpuOff,
                              IReadOnlyList<string> Kept)
{
    public bool IsEmpty => RevertBoosts.Count == 0 && FrameGen == null && Gpu == null;
}

public static class PcApplyPlanner
{
    public const string NothingToApply = "적용할 값이 없어요 (바뀐 것 없음)";
    public const string AlreadyOn = "이미 켜져 있어요 (바뀐 것 없음)";
    public const string GpuAlreadyTuned = "오버클럭 / 전력 제한이 이미 켜져 있어서 그대로 뒀어요";
    public const string GpuNoConsent = "오버클럭 안내에 동의하지 않아서 전력 제한은 바꾸지 않았어요";
    public const string FgKept = "프레임 생성 설정은 그 뒤에 바뀌어서 그대로 뒀어요";
    public const string GpuKept = "GPU 전력 제한 값은 그 뒤에 바뀌어서 그대로 뒀어요";

    public static ApplyPlan Plan(IReadOnlyList<Recommendation> chosen, PcApplyState state, PcApplyRecord? existing,
                                 DateTime now, FgAllowance allowance)
    {
        var boosts = new List<ApplyStep>();
        ApplyStep? fg = null, gpu = null;
        var skipped = new List<ApplyResult>();
        var order = new List<string>();
        var seen = new HashSet<string>();
        foreach (var r in chosen)
        {
            if (r == null || !r.Applicable || !seen.Add(r.Id)) continue;   // tips are shown, never applied
            order.Add(r.Id);
            switch (r.Kind)
            {
                case RecKind.Boost:
                    if (string.IsNullOrEmpty(r.BoostFeatureId)) skipped.Add(new ApplyResult(r.Id, false, NothingToApply));
                    else if (state.BoostsOn.Contains(r.BoostFeatureId)) skipped.Add(new ApplyResult(r.Id, true, AlreadyOn));
                    else if (boosts.Any(b => b.BoostId == r.BoostFeatureId)) order.Remove(r.Id);
                    else boosts.Add(new ApplyStep(r.Id, ApplyStepKind.Boost, BoostId: r.BoostFeatureId));
                    break;
                case RecKind.FrameGen:
                    if (r.FrameGen == null) skipped.Add(new ApplyResult(r.Id, false, NothingToApply));
                    else if (fg != null) order.Remove(r.Id);   // one choice: the first
                    else fg = new ApplyStep(r.Id, ApplyStepKind.FrameGen, FrameGen: FgChoiceRules.Fit(r.FrameGen, allowance));
                    break;
                case RecKind.Gpu:
                    if (r.PowerW is not > 0) skipped.Add(new ApplyResult(r.Id, false, NothingToApply));
                    else if (gpu != null) order.Remove(r.Id);
                    else if (state.GpuProblem != null) skipped.Add(new ApplyResult(r.Id, false, state.GpuProblem));
                    else if (state.GpuTuned) skipped.Add(new ApplyResult(r.Id, false, GpuAlreadyTuned));
                    else if (!state.GpuConsent) skipped.Add(new ApplyResult(r.Id, false, GpuNoConsent));
                    else gpu = new ApplyStep(r.Id, ApplyStepKind.Gpu, PowerW: r.PowerW.Value);
                    break;
                default:
                    order.Remove(r.Id);
                    break;
            }
        }
        var steps = boosts.ToList();
        if (fg != null) steps.Add(fg);
        if (gpu != null) steps.Add(gpu);

        var before = existing ?? new PcApplyRecord(now, Array.Empty<string>());
        var record = new PcApplyRecord(
            now,
            (before.Boosts ?? Array.Empty<string>()).Concat(boosts.Select(b => b.BoostId!)).Distinct().ToList(),
            fg == null ? before.FrameGen
                : before.FrameGen is { } f ? f with { Applied = fg.FrameGen! } : new FgUndo(state.FgNow, state.FgSaved, fg.FrameGen!),
            gpu == null ? before.Gpu
                : before.Gpu is { } g ? g with { AppliedW = gpu.PowerW } : new GpuUndo(state.GpuChoice, gpu.PowerW));
        return new ApplyPlan(steps, skipped, steps.Count == 0 ? before : record, order);
    }

    /// The record once the steps ran: boost items that did not come on are not ours to turn off; a GPU step that failed
    /// (the runner put the GPU page's values back at once) leaves only what an earlier apply recorded.
    public static PcApplyRecord Settle(PcApplyRecord planned, PcApplyRecord? existing, IReadOnlyCollection<string> boostsOnNow, bool gpuFailed) =>
        planned with
        {
            Boosts = planned.Boosts.Where(boostsOnNow.Contains).ToList(),
            Gpu = gpuFailed ? existing?.Gpu : planned.Gpu,
        };

    public static UndoPlan Undo(PcApplyRecord record, PcUndoState now)
    {
        var kept = new List<string>();
        var boosts = (record.Boosts ?? Array.Empty<string>()).Reverse().Where(now.BoostsOn.Contains).ToList();
        FgUndo? fg = null;
        if (record.FrameGen is { } f)
        {
            if (FgChoiceRules.Same(now.FgNow, f.Applied)) fg = f;
            else kept.Add(FgKept);
        }
        GpuUndo? gpu = null;
        if (record.Gpu is { } g)
        {
            if (now.GpuChoice == new GpuValues(g.AppliedW, 0, 0)) gpu = g;
            else kept.Add(GpuKept);
        }
        return new UndoPlan(boosts, fg, gpu, gpu != null && now.GpuTuned, kept);
    }

    /// What is left on record after an undo: only what was to go back and did not (it can be tried again).
    public static PcApplyRecord AfterUndo(PcApplyRecord record, UndoPlan plan, IReadOnlyCollection<string> boostsStillOn,
                                          bool gpuStillTuned, DateTime now) =>
        new(now, plan.RevertBoosts.Where(boostsStillOn.Contains).Reverse().ToList(), null,
            plan.TurnGpuOff && gpuStillTuned ? record.Gpu : null);
}
