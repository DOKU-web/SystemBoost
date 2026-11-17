// The GPU features through IGpuVendorBackend (Services/Gpu).
//   NVIDIA  NvidiaGpuBackend below: a thin adapter that calls the code that was here before, unchanged
//           (ApplyDriverAsync, ApplyGpuTuningAsync, KeepGpuAsync, RestoreGpuAsync, WatchGpuAsync, "nv.restore").
using System;
using System.IO;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed partial class WindowsBoostBackend
{
    private NvidiaGpuBackend? _nvidiaGpu;

    private NvidiaGpuBackend Nvidia => _nvidiaGpu ??= new NvidiaGpuBackend(this);

    private static string System32(string file) => Path.Combine(Environment.SystemDirectory, file);

    // ------------------------------------------------------------ NVIDIA
    /// The NVIDIA code as it was, behind the interface. Nothing here decides anything of its own.
    private sealed class NvidiaGpuBackend : IGpuVendorBackend
    {
        private readonly WindowsBoostBackend _b;
        public NvidiaGpuBackend(WindowsBoostBackend b) { _b = b; }

        public GpuVendor Vendor => GpuVendor.Nvidia;
        public string Name => Nvml.Read()?.Name ?? "";
        public Task<bool> DetectAsync() => Task.FromResult(Nvml.Read() != null || File.Exists(System32("nvapi64.dll")));
        public GpuCapabilities GetCapabilities() => GpuCapabilities.NvidiaAsBefore;
        public Task<GpuTelemetry?> ReadTelemetryAsync() =>
            Task.FromResult(Nvml.Read() is { } g ? GpuTelemetry.Empty with { TempC = g.TempC } : null);
        public Task<string> ApplyGameProfileAsync(string? game) => _b.ApplyDriverAsync();
        public Task<string?> RevertGameProfileAsync(BoostBackup b) => _b.HelperError("nv.restore", b.Target, b.Name, b.Value);
        public Task<string> ApplyTuningAsync() => _b.ApplyGpuTuningAsync();
        public Task<string> ApplyAutoTuningAsync(GpuAutoTuning kind) => Task.FromResult(GpuCapabilities.NotOnThisCard + " (바뀐 것 없음)");
        public Task<string?> RevertTuningAsync(BoostBackup b) => _b.RestoreGpuAsync(b);
        public Task<string> ResetToFactoryAsync() => Task.FromResult(GpuCapabilities.NotOnThisCard + " (바뀐 것 없음)");
        public Task<string> KeepTuningAsync() => _b.KeepGpuAsync();
        public Task WatchTuningAsync() => _b.WatchGpuAsync();
    }
}
