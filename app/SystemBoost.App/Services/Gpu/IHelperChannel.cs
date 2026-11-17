// One line to sb_helper.exe, one reply back ("ok\t<data>" or "err\t<why>"). BoostHelper is the real one;
// the GPU tests (app/SystemBoost.Gpu.Tests) put a fake helper behind it.
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

internal interface IHelperChannel
{
    bool Running { get; }
    /// Null = ready, otherwise the text to show. Nothing changed in either case.
    Task<string?> EnsureAsync();
    /// Fields are joined with tabs and must not contain tabs or newlines.
    Task<(bool ok, string data)> CallAsync(params string[] fields);
}
