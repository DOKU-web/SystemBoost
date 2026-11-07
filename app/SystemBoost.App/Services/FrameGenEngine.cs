// Starts the native frame generation engine for the current OS and streams its
// JSON lines. Windows: native/framegen/sb_framegen.exe (D3D11).
// macOS: sb-framegen-mac next to the app executable (Metal). Same protocol.
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed record GameWindow(ulong Id, string App, string Title, int Width, int Height)
{
    public override string ToString() =>
        (string.IsNullOrEmpty(App) ? Title : $"{App} — {Title}") + $"  ({Width}×{Height})";
}

public sealed record GpuItem(string Value, string Name, bool Integrated)
{
    public override string ToString() => Name + (Integrated ? " (내장)" : "");
}

public sealed record FrameGenOptions(string Mode, int MaxMultiplier, string Quality, bool Upscale,
                                     double Sharpness, string GenGpu, string Capture, int TargetFps);

public sealed record FrameGenStats(double SrcFps, double OutFps, double Multiplier, double RefreshHz,
                                   double DelayMs, double AddedLatencyMs, double GenMs, double AnalyzeMs,
                                   double TransferMs, long Late, long Skipped, int Mode, int Divider,
                                   long SrcFrames, long OutFrames, double ActiveSec);

public static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static string Name => IsWindows ? "Windows" : IsMac ? "macOS" : "Linux";

    public static string EnginePath => IsWindows
        ? Path.Combine(AppContext.BaseDirectory, "native", "framegen", "sb_framegen.exe")
        : Path.Combine(AppContext.BaseDirectory, "sb-framegen-mac");

    public static bool EngineAvailable => File.Exists(EnginePath);

    /// Portable build: the Velopack portable zip (Windows), or portable.txt next to the exe (dev / older zips).
    /// Data stays next to the app.
    public static bool Portable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")) || (IsWindows && Updater.IsPortable);

    public static string DataDir
    {
        get
        {
            // Velopack keeps the app in <folder>\current and replaces that folder on update,
            // so portable data goes one level up (<folder>\data) to survive updates.
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.GetFileName(baseDir).Equals("current", StringComparison.OrdinalIgnoreCase)) baseDir = Path.GetDirectoryName(baseDir)!;
            string dir = Portable
                ? Path.Combine(baseDir, "data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SystemBoost");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

public sealed class FrameGenEngine : IDisposable
{
    private Process? _p;
    private bool _upscale;
    private StreamWriter? _log;     // what the engine reported in the last run: frame rates and timings only
    private long _logged;
    private const long MaxLogBytes = 2_000_000;
    public event Action<FrameGenStats>? Stats;
    public event Action<JsonElement>? Ready;
    public event Action<string, string>? Message;   // (warning|error, text)
    public event Action? FitStopped;                // the engine stopped generating and shows the game as it is
    public event Action? Exited;
    public bool Running => _p is { HasExited: false };

    public void Start(ulong windowId, FrameGenOptions o)
    {
        Stop();
        var inv = CultureInfo.InvariantCulture;
        var args = $"--hwnd {windowId} --mode {o.Mode} --max-mul {o.MaxMultiplier} --quality {o.Quality} " +
                   $"--sharpness {o.Sharpness.ToString("0.00", inv)} --gen-gpu {o.GenGpu}" +
                   (Platform.IsWindows ? $" --capture {o.Capture}" : "") + (o.Upscale ? " --upscale" : "") +
                   (o.TargetFps > 0 ? $" --target-fps {o.TargetFps}" : "");
        var psi = new ProcessStartInfo(Platform.EnginePath, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,   // the engine writes UTF-8; the default is the ANSI code page
        };
        _upscale = o.Upscale;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        OpenLog();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { WriteLog(e.Data); OnLine(e.Data); } };
        p.ErrorDataReceived += (_, _) => { };
        p.Exited += (_, _) =>
        {
            RestoreCursorsIfNeeded();
            Dispatcher.UIThread.Post(() => Exited?.Invoke());
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _p = p;
    }

    private void OpenLog()
    {
        try
        {
            lock (this)
            {
                _log?.Dispose();
                _logged = 0;
                _log = new StreamWriter(Path.Combine(Platform.DataDir, "framegen-last.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            }
        }
        catch (Exception) { _log = null; }
    }

    private void WriteLog(string line)
    {
        lock (this)
        {
            if (_log == null || _logged > MaxLogBytes) return;
            try { _log.WriteLine(line); _logged += line.Length; }
            catch (Exception) { _log = null; }
        }
    }

    public void SetMode(string mode) => Send($"mode {mode}");
    public void SetTarget(int fps) => Send($"target {fps}");
    public void SetSharpness(double s) => Send($"sharp {s.ToString("0.00", CultureInfo.InvariantCulture)}");
    public void Enable(bool on) => Send(on ? "enable" : "disable");

    public void Stop()
    {
        var p = _p;
        _p = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                Send(p, "quit");
                p.StandardInput.Close();   // the engine also quits on stdin EOF
                if (!p.WaitForExit(2000)) p.Kill();
            }
        }
        catch (InvalidOperationException) { }
        p.Dispose();
    }

    public void Dispose() => Stop();

    private void Send(string cmd) { if (_p != null) Send(_p, cmd); }
    private static void Send(Process p, string cmd)
    {
        try { if (!p.HasExited) { p.StandardInput.WriteLine(cmd); p.StandardInput.Flush(); } }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private void OnLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement.Clone();
            var type = r.GetProperty("type").GetString();
            Dispatcher.UIThread.Post(() =>
            {
                switch (type)
                {
                    case "stats": Stats?.Invoke(ParseStats(r)); break;
                    case "ready": Ready?.Invoke(r); break;
                    case "fit":
                        Message?.Invoke("warning", FitText(r));
                        if (r.TryGetProperty("stopped", out _)) FitStopped?.Invoke();
                        break;
                    case "warning":
                    case "error": Message?.Invoke(type!, r.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : ""); break;
                }
            });
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
    }

    private static string FitText(JsonElement r)
    {
        if (r.TryGetProperty("stopped", out _)) return "GPU 여유가 없어 프레임 생성을 멈췄어요. 게임 화면을 그대로 보여 줘요.";
        if (r.TryGetProperty("maxOut", out _)) return "출력이 계속 밀려서 출력 프레임 수를 낮췄어요.";
        var quality = r.TryGetProperty("quality", out var q) ? q.GetString() : null;
        var name = quality switch { "low" => "저사양", "performance" => "성능", _ => "균형" };
        return $"출력이 계속 밀려서 품질을 '{name}' 단계로 낮췄어요. 다음에 시작할 때는 원래 설정으로 돌아가요.";
    }

    private static double D(JsonElement r, string k) => r.TryGetProperty(k, out var v) ? v.GetDouble() : 0;
    private static FrameGenStats ParseStats(JsonElement r) => new(
        D(r, "srcFps"), D(r, "outFps"), D(r, "multiplier"), D(r, "refreshHz"), D(r, "delayMs"),
        D(r, "addedLatencyMs"), D(r, "genMs"), D(r, "analyzeMs"), D(r, "transferMs"),
        (long)D(r, "late"), (long)D(r, "skipped"), (int)D(r, "mode"), (int)D(r, "divider"),
        (long)D(r, "srcFrames"), (long)D(r, "outFrames"), D(r, "activeSec"));

    // ---- one-shot queries ---------------------------------------------------
    private static async Task<JsonElement?> QueryAsync(string arg)
    {
        if (!Platform.EngineAvailable) return null;
        var psi = new ProcessStartInfo(Platform.EnginePath, arg)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi);
        if (p == null) return null;
        string all = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        foreach (var line in all.Split('\n'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var t = doc.RootElement.GetProperty("type").GetString();
                if (t == "windows" || t == "gpus") return doc.RootElement.Clone();
                if (t == "error") return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return null;
    }

    public static async Task<(List<GameWindow> list, string? error)> ListWindowsAsync()
    {
        var r = await QueryAsync("--list-windows");
        var list = new List<GameWindow>();
        if (r is not JsonElement e) return (list, "엔진을 찾을 수 없어요");
        if (e.GetProperty("type").GetString() == "error") return (list, e.GetProperty("msg").GetString());
        foreach (var w in e.GetProperty("list").EnumerateArray())
            list.Add(new GameWindow(w.GetProperty("id").GetUInt64(), w.GetProperty("app").GetString() ?? "",
                                    w.GetProperty("title").GetString() ?? "", w.GetProperty("w").GetInt32(),
                                    w.GetProperty("h").GetInt32()));
        return (list, null);
    }

    public static async Task<List<GpuItem>> ListGpusAsync()
    {
        var list = new List<GpuItem> { new("same", "화면 GPU (기본)", false) };
        var r = await QueryAsync("--list-gpus");
        if (r is not JsonElement e || e.GetProperty("type").GetString() != "gpus") return list;
        foreach (var g in e.GetProperty("list").EnumerateArray())
            list.Add(new GpuItem(g.GetProperty("index").GetInt32().ToString(CultureInfo.InvariantCulture),
                                 g.GetProperty("name").GetString() ?? "GPU", g.GetProperty("integrated").GetBoolean()));
        return list;
    }

    // If the engine was killed while upscaling on Windows, put the cursor scheme back.
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint a, uint p, IntPtr v, uint f);
    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
    private void RestoreCursorsIfNeeded()
    {
        if (!_upscale || !Platform.IsWindows) return;
        SystemParametersInfoW(0x0057 /* SPI_SETCURSORS */, 0, IntPtr.Zero, 0);
        ClipCursor(IntPtr.Zero);
    }
}
