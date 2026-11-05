// SystemBoost (WPF, .NET 8) <-> sb_framegen.exe bridge.
// Drop into the WPF project and adapt the namespace. Starts the native process
// for a game window, streams its JSON lines as events, sends commands.
// The native process exits on its own when stdin closes, so if the app crashes
// the overlay does not stay behind.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemBoost.FrameGen;

public sealed record FrameGenOptions(
    string Mode = "auto",          // auto | x2 | x3 | x4 | off
    int MaxMultiplier = 4,         // 2..4 (auto)
    string Quality = "balanced",   // performance | balanced | quality
    bool Upscale = false,
    double Sharpness = 0.5,        // 0..1
    string GenGpu = "same",        // same | igpu | <adapter index from ListGpus>
    string Capture = "wgc");       // wgc | dxgi

public sealed record FrameGenStats(
    double SrcFps, double OutFps, double Multiplier, double RefreshHz,
    double DelayMs, double AddedLatencyMs, double GenMs, double AnalyzeMs,
    double TransferMs, long Late, long Skipped);

public sealed class FrameGenProcess : IDisposable
{
    private readonly Process _p;
    public event Action<FrameGenStats>? Stats;
    public event Action<JsonElement>? Ready;
    public event Action<string, string>? Message;   // (type, msg): warning | error
    public event Action? Exited;

    public static string ExePath =>
        Path.Combine(AppContext.BaseDirectory, "native", "framegen", "sb_framegen.exe");

    private FrameGenProcess(Process p) { _p = p; }

    public static FrameGenProcess Start(IntPtr gameHwnd, FrameGenOptions o)
    {
        var args = $"--hwnd 0x{gameHwnd.ToInt64():X} --mode {o.Mode} --max-mul {o.MaxMultiplier} " +
                   $"--quality {o.Quality} --sharpness {o.Sharpness.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                   $"--gen-gpu {o.GenGpu} --capture {o.Capture}" + (o.Upscale ? " --upscale" : "");
        var psi = new ProcessStartInfo(ExePath, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var fg = new FrameGenProcess(p);
        p.OutputDataReceived += (_, e) => { if (e.Data != null) fg.OnLine(e.Data); };
        p.Exited += (_, _) => { RestoreCursorsIfNeeded(o.Upscale); fg.Exited?.Invoke(); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return fg;
    }

    /// <summary>Adapters for the "frame generation GPU" dropdown.</summary>
    public static async Task<JsonElement?> ListGpusAsync()
    {
        var psi = new ProcessStartInfo(ExePath, "--list-gpus")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string line = await p.StandardOutput.ReadLineAsync() ?? "";
        await p.WaitForExitAsync();
        try { return JsonDocument.Parse(line).RootElement.Clone(); } catch { return null; }
    }

    public void SetMode(string mode) => Send($"mode {mode}");
    public void SetSharpness(double s) => Send($"sharp {s.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    public void Enable(bool on) => Send(on ? "enable" : "disable");

    private void Send(string cmd)
    {
        try { if (!_p.HasExited) { _p.StandardInput.WriteLine(cmd); _p.StandardInput.Flush(); } }
        catch (IOException) { /* process gone */ }
    }

    private void OnLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            switch (r.GetProperty("type").GetString())
            {
                case "stats":
                    Stats?.Invoke(new FrameGenStats(
                        r.GetProperty("srcFps").GetDouble(), r.GetProperty("outFps").GetDouble(),
                        r.GetProperty("multiplier").GetDouble(), r.GetProperty("refreshHz").GetDouble(),
                        r.GetProperty("delayMs").GetDouble(), r.GetProperty("addedLatencyMs").GetDouble(),
                        r.GetProperty("genMs").GetDouble(), r.GetProperty("analyzeMs").GetDouble(),
                        r.GetProperty("transferMs").GetDouble(), r.GetProperty("late").GetInt64(),
                        r.GetProperty("skipped").GetInt64()));
                    break;
                case "ready": Ready?.Invoke(r.Clone()); break;
                case "warning":
                case "error": Message?.Invoke(r.GetProperty("type").GetString()!, r.GetProperty("msg").GetString() ?? ""); break;
            }
        }
        catch (JsonException) { /* ignore partial lines */ }
    }

    // If the native process was killed while upscaling, put the user's cursor
    // scheme back (the native side restores it on every normal exit path).
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint flags);
    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
    private static void RestoreCursorsIfNeeded(bool upscale)
    {
        if (!upscale) return;
        const uint SPI_SETCURSORS = 0x0057;
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        ClipCursor(IntPtr.Zero);
    }

    public void Dispose()
    {
        try
        {
            if (!_p.HasExited)
            {
                Send("quit");
                _p.StandardInput.Close();          // native side also quits on stdin EOF
                if (!_p.WaitForExit(2000)) _p.Kill();
            }
        }
        catch (InvalidOperationException) { }
        _p.Dispose();
    }
}
