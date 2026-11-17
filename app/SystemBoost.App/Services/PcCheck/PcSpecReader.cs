// PC 맞춤 추천: reads the specs on Windows, without administrator rights and without changing anything.
// Owner: the advisor part (CONTRACT.md).
using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace SystemBoost.App.Services.PcCheck;

[SupportedOSPlatform("windows")]
public static class PcSpecReader
{
    /// Unknown values stay 0, "" or null; never throws.
    public static Task<PcSpecs> ReadAsync() =>
        throw new NotImplementedException("PcSpecReader: the advisor part fills this in");
}
