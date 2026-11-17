// PC 맞춤 추천: the rules from specs and measurements to recommendations. Owner: the advisor part (CONTRACT.md).
using System;
using System.Collections.Generic;

namespace SystemBoost.App.Services.PcCheck;

public static class PcAdvisor
{
    /// Every recommendation and tip for this PC, in the order the page shows them. Never throws.
    public static IReadOnlyList<Recommendation> Recommend(PcSpecs specs, BenchResults bench, PcNow now) =>
        throw new NotImplementedException("PcAdvisor: the advisor part fills this in");
}
