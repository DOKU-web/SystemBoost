// One record of boost-state.json (WindowsBoostBackend). Kept in its own file with no other dependency, so the
// GPU tests (app/SystemBoost.Gpu.Tests) can link it.
namespace SystemBoost.App.Services;

/// One saved value. Kind says how to put it back.
public sealed class BoostBackup
{
    public string Feature { get; set; } = "";
    public string Kind { get; set; } = "";     // hkcu, hags, ifeo, netpm, nv, nvlimit, power, wu, ecores, oc, startup, amdoc, amd3d
    public string Target { get; set; } = "";   // game path, exe name or adapter
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public bool Dword { get; set; }
    public string Value { get; set; } = "";
    public string ValueKind { get; set; } = "";   // hkcu: the registry type before (records from before this: Dword)
    public string SetTo { get; set; } = "";       // what SystemBoost wrote; "" in records from before this
}
