// Starts the native frame generation engine for the current OS and streams its
// JSON lines. Windows: native/framegen/sb_framegen.exe (D3D11).
// macOS: sb-framegen-mac next to the app executable (Metal). Same protocol.
// Windows also: a watchdog that ends an engine gone silent (its window would stay over the game), and the
// record of the frame limit frame generation writes into a game's NVIDIA driver profile (bottom of the file).
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
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
                                   long SrcFrames, long OutFrames, double ActiveSec, bool Idle = false,
                                   bool Trial = false);

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
    private StreamWriter? _log;     // what the engine reported in a run: frame rates and timings only
    private long _logged;
    private const long MaxLogBytes = 2_000_000;
    private const int KeepLogs = 8;         // runs are compared with each other, say with a game setting on and off
    public event Action<FrameGenStats>? Stats;
    public event Action<JsonElement>? Ready;
    public event Action<string, string>? Message;   // (warning|error, text)
    public event Action? FitStopped;                // the engine stopped generating and shows the game as it is
    /// The engine stopped answering and was ended: by this app, or by itself when the app could not (exit
    /// code 7). Comes before Exited, on the UI thread.
    public event Action? Stalled;
    public event Action? Exited;
    public bool Running => _p is { HasExited: false };

    // A stuck engine leaves its last frame in a topmost window over the game and, while upscaling, the cursor
    // blank and held inside the game. Its main loop says "alive" about every second, also while the game is
    // behind another window and no stats come (native/framegen/src/main.cpp), so a silence means it is stuck.
    public const int StallSeconds = 8;
    private const long StartGraceMs = 30_000;   // before the first "alive": devices, shaders and capture start up
    private const long SleptMs = 3_000;          // the watch itself was held this long: the PC slept, judge nothing
    private const int ExitStalled = 7;           // main.cpp: the engine's own watchdog ended it
    private const string AliveLine = "{\"type\":\"alive\"}";
    private Timer? _watch;
    private long _lastLine, _lastWatch;
    private volatile bool _beats, _heard;   // "alive" came / any line came

    public void Start(ulong windowId, FrameGenOptions o)
    {
        Stop();
        var inv = CultureInfo.InvariantCulture;
        // --parent-pid: the engine ends with this process even if stdin stays open somewhere
        var args = $"--hwnd {windowId} --mode {o.Mode} --max-mul {o.MaxMultiplier} --quality {o.Quality} " +
                   $"--sharpness {o.Sharpness.ToString("0.00", inv)} --gen-gpu {o.GenGpu}" +
                   (Platform.IsWindows ? $" --capture {o.Capture} --parent-pid {Environment.ProcessId}" : "") +
                   (o.Upscale ? " --upscale" : "") + (o.TargetFps > 0 ? $" --target-fps {o.TargetFps}" : "");
        var psi = new ProcessStartInfo(Platform.EnginePath, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,   // the engine writes UTF-8; the default is the ANSI code page
        };
        bool upscale = o.Upscale;   // this run's: a later Start must not decide for it
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        OpenLog(args);
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data != AliveLine) WriteLog(e.Data);   // the run logs keep frame rates and timings
            OnLine(e.Data);
        };
        p.ErrorDataReceived += (_, _) => { };
        p.Exited += (_, _) =>
        {
            RestoreCursorsIfNeeded(upscale);
            bool stalled = false;
            try { stalled = p.ExitCode == ExitStalled; } catch (Exception) { }
            Dispatcher.UIThread.Post(() => { if (stalled) Stalled?.Invoke(); Exited?.Invoke(); });
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _p = p;
        if (Platform.IsWindows)   // the Mac engine sends no "alive"
        {
            Interlocked.Exchange(ref _lastLine, Environment.TickCount64);
            Interlocked.Exchange(ref _lastWatch, Environment.TickCount64);
            _beats = _heard = false;
            _watch = new Timer(_ => Watch(p), null, 1000, 1000);
        }
    }

    private void Watch(Process p)
    {
        long now = Environment.TickCount64;
        long sinceWatch = now - Interlocked.Exchange(ref _lastWatch, now);
        if (!ReferenceEquals(p, _p)) return;
        if (sinceWatch > SleptMs) { Interlocked.Exchange(ref _lastLine, now); return; }   // the engine was held as well
        if (_heard && !_beats) return;   // an engine that talks but sends no "alive" (an older build): nothing to judge by
        long quiet = now - Interlocked.Read(ref _lastLine);
        if (quiet < (_beats ? StallSeconds * 1000L : StartGraceMs)) return;
        StopWatch();
        Dispatcher.UIThread.Post(() => Stalled?.Invoke());
        try { if (!p.HasExited) p.Kill(); }   // its window goes with it; Exited puts the cursor back
        catch (Exception) { }
    }

    private void StopWatch()
    {
        var w = Interlocked.Exchange(ref _watch, null);
        w?.Dispose();
    }

    private void OpenLog(string args)
    {
        try
        {
            lock (this)
            {
                _log?.Dispose();
                _logged = 0;
                var dir = Directory.CreateDirectory(Path.Combine(Platform.DataDir, "framegen-logs"));
                foreach (var old in dir.GetFiles("*.log").OrderByDescending(f => f.Name).Skip(KeepLogs - 1)) old.Delete();
                var name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log";
                _log = new StreamWriter(Path.Combine(dir.FullName, name), false, new UTF8Encoding(false)) { AutoFlush = true };
                _log.WriteLine(args);
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
        StopWatch();
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
        Interlocked.Exchange(ref _lastLine, Environment.TickCount64);   // any line: the engine is not stuck
        if (line == AliveLine) { _beats = true; return; }               // for the watchdog only
        _heard = true;
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
                        // resting near the limit, or a trial run that passed: the stats lines say so
                        if (r.TryGetProperty("idle", out _) || r.TryGetProperty("shown", out _)) break;
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
        if (r.TryGetProperty("nogain", out _)) return "프레임 생성이 게임 혼자일 때보다 프레임을 더 내지 못해서 멈췄어요. 게임 화면을 그대로 보여 줘요.";
        if (r.TryGetProperty("trial", out _)) return "게임 혼자일 때보다 프레임이 늘지 않아서 품질을 '저사양' 단계로 낮춰 다시 재 보고 있어요. 그동안 게임 화면을 그대로 보여 줘요.";
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
        (long)D(r, "srcFrames"), (long)D(r, "outFrames"), D(r, "activeSec"),
        r.TryGetProperty("idle", out var idle) && idle.ValueKind == JsonValueKind.True,
        r.TryGetProperty("trial", out var trial) && trial.ValueKind == JsonValueKind.True);

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
    private static void RestoreCursorsIfNeeded(bool upscale)
    {
        if (!upscale || !Platform.IsWindows) return;
        SystemParametersInfoW(0x0057 /* SPI_SETCURSORS */, 0, IntPtr.Zero, 0);
        ClipCursor(IntPtr.Zero);
    }

    // ---- the frame limit in the NVIDIA driver profile (fg.limit) ----------------------------
    // When the game leaves frame generation no room, FrameGenViewModel asks the limiter for a driver frame
    // limit on the game (WindowsBoostBackend, through sb_helper). That limit sits in the game's NVIDIA driver
    // profile, where only the NVIDIA app shows it. It is written here, with the value the profile held
    // before, ahead of the driver write, and taken off this list only once the profile no longer holds our
    // number. So a crash, a power cut or an uninstall in between cannot leave a game held at 20 fps.
    // Fps: every number written for this game since Old was read, newest last. Old: "none" or a number.
    public sealed record DriverLimit(string Exe, List<int> Fps, string Old);

    private static readonly SemaphoreSlim DriverGate = new(1, 1);
    private static string DriverLimitFile => Path.Combine(Platform.DataDir, "fg-driver-limit.json");
    private static bool HasNvidiaDriver =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));
    private const string ReadFailed = "드라이버의 지금 프레임 제한 값을 먼저 기록하지 못해서 게임 프레임을 제한하지 않았어요: ";

    /// What an earlier run left in a driver profile goes back first (FrameGenViewModel starts it with the app).
    internal static Task<string?> StartupRestore { get; private set; } = Task.FromResult<string?>(null);
    private static int _startupStarted;

    internal static Task<string?> RestoreAtStartup()
    {
        if (Interlocked.Exchange(ref _startupStarted, 1) == 0 && OperatingSystem.IsWindows())
            StartupRestore = Task.Run(() => RestoreDriverSettingsAsync());
        return StartupRestore;
    }

    /// Before a frame limit is asked for: the value the game's profile holds now goes to disk. error null =
    /// go ahead; otherwise nothing was written and nothing may be. added: undo it with ForgetDriverLimitAsync
    /// if the limit is not written after all.
    internal static async Task<(string? error, bool added)> RecordDriverLimitAsync(string exe, int fps)
    {
        if (!HasNvidiaDriver) return (null, false);   // the limiter writes nothing there, it only says what to set
        await StartupRestore;   // an earlier run's number must not be read as the value from before
        await DriverGate.WaitAsync();
        try
        {
            var list = LoadDriverLimits();
            var entry = list.Find(e => string.Equals(e.Exe, exe, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                using var helper = new BoostHelper(elevated: false);
                var e = await helper.EnsureAsync();
                if (e != null) return (ReadFailed + e, false);
                var (ok, data) = await helper.CallAsync("nv.limit.get", exe);
                if (!ok) return data == "no_nvidia" ? (null, false) : (ReadFailed + data, false);
                entry = new DriverLimit(exe, new List<int>(), data);
                list.Add(entry);
            }
            entry.Fps.Add(fps);
            SaveDriverLimits(list);
            return (null, true);
        }
        catch (Exception e) { return (ReadFailed + e.Message, false); }
        finally { DriverGate.Release(); }
    }

    /// The limit RecordDriverLimitAsync was for was not written (refused, failed, or a lower one was there).
    internal static async Task ForgetDriverLimitAsync(string exe, int fps)
    {
        await DriverGate.WaitAsync();
        try
        {
            var list = LoadDriverLimits();
            var entry = list.Find(e => string.Equals(e.Exe, exe, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            int i = entry.Fps.LastIndexOf(fps);
            if (i >= 0) entry.Fps.RemoveAt(i);
            if (entry.Fps.Count == 0) list.Remove(entry);
            SaveDriverLimits(list);
        }
        catch (Exception) { }   // a number left on the list only costs a check later: nothing is put back unless it is ours
        finally { DriverGate.Release(); }
    }

    /// Puts back the frame limit frame generation left in a game's NVIDIA driver profile, while the profile
    /// still holds the number it wrote: a limit set since, by the user or another program, stays. For the
    /// start after a crash, 전체 되돌리기 and the uninstall hook. Null when nothing is left over, otherwise
    /// why something is (it stays on the list for the next try). mayElevate: may ask for administrator
    /// rights when the plain helper is refused (older drivers); never without a window. onlyExe: one game's
    /// (the limit frame generation just ended), so a limit another game holds right now is not touched.
    public static async Task<string?> RestoreDriverSettingsAsync(bool mayElevate = false, string? onlyExe = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        await DriverGate.WaitAsync();
        try
        {
            var list = LoadDriverLimits();
            if (list.Count == 0) return null;
            string? failed = null;
            BoostHelper? plain = null, admin = null;
            try
            {
                foreach (var entry in list.ToList())
                {
                    if (onlyExe != null && !string.Equals(entry.Exe, onlyExe, StringComparison.OrdinalIgnoreCase)) continue;
                    var e = await UndoDriverLimitAsync(plain ??= new BoostHelper(elevated: false), entry);
                    if (e != null && mayElevate) e = await UndoDriverLimitAsync(admin ??= new BoostHelper(), entry);
                    if (e == null) list.Remove(entry);
                    else failed = e;
                }
            }
            finally
            {
                plain?.Dispose();
                admin?.Dispose();
            }
            SaveDriverLimits(list);
            return failed;
        }
        catch (Exception e) { return e.Message; }
        finally { DriverGate.Release(); }
    }

    /// A frame limit of ours is on record in some game's driver profile (전체 되돌리기 counts it).
    internal static bool HasDriverLimits
    {
        get { try { return OperatingSystem.IsWindows() && LoadDriverLimits().Count > 0; } catch (Exception) { return false; } }
    }

    /// The same for the Velopack uninstall hook (Program.cs): no window and no elevation there, so the plain
    /// helper only. Waits at most 10 seconds and never throws.
    public static void RestoreDriverSettings()
    {
        try { Task.Run(() => RestoreDriverSettingsAsync()).Wait(TimeSpan.FromSeconds(10)); }
        catch (Exception) { }
    }

    // Null when this game's profile no longer holds any number of ours.
    private static async Task<string?> UndoDriverLimitAsync(BoostHelper helper, DriverLimit entry)
    {
        var e = await helper.EnsureAsync();
        if (e != null) return e;
        for (int i = entry.Fps.Count - 1; i >= 0; i--)
        {
            var fps = entry.Fps[i].ToString(CultureInfo.InvariantCulture);
            var (ok, data) = await helper.CallAsync("nv.limit.undo", entry.Exe, fps, entry.Old);
            if (!ok) return data == "no_nvidia" ? null : data;   // no NVIDIA driver any more: no profile holds it
            if (data != "changed") return null;                  // put back, or the profile or setting is gone
        }
        return null;   // another number now: the user's or another program's, left alone
    }

    private static List<DriverLimit> LoadDriverLimits()
    {
        try
        {
            if (File.Exists(DriverLimitFile))
                return JsonSerializer.Deserialize<List<DriverLimit>>(File.ReadAllText(DriverLimitFile)) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    // Flushed to the disk before the driver is written: a power cut right after must still find it.
    private static void SaveDriverLimits(List<DriverLimit> list)
    {
        var file = DriverLimitFile;
        if (list.Count == 0) { File.Delete(file); return; }
        var tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(JsonSerializer.SerializeToUtf8Bytes(list));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, file, true);
    }
}
