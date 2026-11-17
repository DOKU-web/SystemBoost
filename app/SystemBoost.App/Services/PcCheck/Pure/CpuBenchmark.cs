// PC 맞춤 추천: a short CPU benchmark in plain .NET (no game, nothing written). Owner: the advisor part (CONTRACT.md).
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemBoost.App.Services.PcCheck;

public static class CpuBenchmark
{
    /// Single-thread and all-thread scores: `repeats` runs of `each` per side, the median, and the spread as NoisePercent.
    /// HighPlan* stay null (the runner measures the high-performance plan with MultiAsync). Cancels with ct.
    public static Task<CpuBench> RunAsync(TimeSpan each, int repeats, CancellationToken ct) =>
        throw new NotImplementedException("CpuBenchmark: the advisor part fills this in");

    /// The all-thread score alone (median of `repeats` runs of `each`) and its spread in percent.
    public static Task<(double Score, double NoisePercent)> MultiAsync(TimeSpan each, int repeats, CancellationToken ct) =>
        throw new NotImplementedException("CpuBenchmark: the advisor part fills this in");
}
