// PC 맞춤 추천: runs the check (specs, CPU benchmark, frame generation benchmark, optional power-limit test), keeps the
// report, applies the chosen recommendations at once and puts them back. Owner: the runner part (CONTRACT.md).
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using SystemBoost.App.ViewModels;

namespace SystemBoost.App.Services.PcCheck;

[SupportedOSPlatform("windows")]
public sealed class PcCheckRunner
{
    public PcCheckRunner(WindowsBoostBackend backend, FrameGenViewModel fg) { }

    public bool Running => false;

    /// Null when the check may start, otherwise the line to show (plan, a game is running, already running).
    public string? CannotStart() => throw new NotImplementedException();

    /// About a minute. testPower: also the power-limit test (desktop NVIDIA only; asks the overclocking notice and the
    /// administrator prompt first). Saved (data\pc-check.json) before it returns.
    public Task<PcCheckReport> RunAsync(Window owner, bool testPower, Action<string> progress, CancellationToken ct) =>
        throw new NotImplementedException();

    /// Applies the chosen applicable recommendations. GPU ones only after OcConsent.EnsureAsync(owner, gpu, fromPreset: true).
    /// A GPU result asks for '유지' within the confirm time (WindowsBoostBackend.GpuConfirmLeft / KeepGpuAsync).
    public Task<IReadOnlyList<ApplyResult>> ApplyAsync(Window owner, IReadOnlyList<Recommendation> chosen) =>
        throw new NotImplementedException();

    /// True when something applied through this page can still be put back (also after an app restart).
    public bool CanUndo => false;

    /// Puts back what the last ApplyAsync changed. Returns the line to show.
    public Task<string> UndoAsync() => throw new NotImplementedException();

    /// The last saved report, or null.
    public static PcCheckReport? LastReport => null;
}
