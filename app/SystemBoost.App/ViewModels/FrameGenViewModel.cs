using SystemBoost.App.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using License = SystemBoost.App.Services.License;

namespace SystemBoost.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SessionRecord(DateTime Start, string Game, string Mode, double AvgSrc, double AvgOut,
                                   double AvgLatency, long Late)
{
    public override string ToString() =>
        $"{Start:MM-dd HH:mm}  {Game}  ·  {Mode}  ·  {AvgSrc:0} → {AvgOut:0} fps  ·  추가 지연 {AvgLatency:0.0} ms  ·  늦은 프레임 {Late}";
}

public sealed class FrameGenViewModel : ViewModelBase, IDisposable
{
    private readonly FrameGenEngine _engine = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public ObservableCollection<GameWindow> Windows { get; } = new();
    public ObservableCollection<GpuItem> Gpus { get; } = new();
    public static ObservableCollection<SessionRecord> Sessions { get; } = LoadSessions();
    private static string SessionsFile => System.IO.Path.Combine(Platform.DataDir, "sessions.json");

    private static ObservableCollection<SessionRecord> LoadSessions()
    {
        try
        {
            if (System.IO.File.Exists(SessionsFile))
                return new(System.Text.Json.JsonSerializer.Deserialize<List<SessionRecord>>(System.IO.File.ReadAllText(SessionsFile)) ?? new());
        }
        catch (Exception) { /* corrupt or unreadable: start empty */ }
        return new();
    }

    private static void SaveSessions()
    {
        try
        {
            var keep = new List<SessionRecord>(Sessions);
            if (keep.Count > 200) keep.RemoveRange(200, keep.Count - 200);
            System.IO.File.WriteAllText(SessionsFile, System.Text.Json.JsonSerializer.Serialize(keep));
        }
        catch (Exception) { }
    }

    private GameWindow? _selectedWindow;
    public GameWindow? SelectedWindow { get => _selectedWindow; set => Set(ref _selectedWindow, value); }
    private GpuItem? _selectedGpu;
    public GpuItem? SelectedGpu { get => _selectedGpu; set => Set(ref _selectedGpu, value); }

    // What the plan may start with: the page opens here, and its 기본값 button comes back here.
    public static string DefaultMode => License.IsPremium ? "auto" : "x2";
    public static int DefaultMaxMul => License.IsPremium ? 4 : 2;

    private string _mode = DefaultMode;
    public string Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value) && Running) _engine.SetMode(value); }
    }
    private int _maxMul = DefaultMaxMul;
    public int MaxMul { get => _maxMul; set => Set(ref _maxMul, value); }
    private string _quality = "balanced";
    public string Quality { get => _quality; set => Set(ref _quality, value); }
    private bool _upscale;
    public bool Upscale { get => _upscale; set => Set(ref _upscale, value); }
    private double _sharpness = 0.5;
    public double Sharpness
    {
        get => _sharpness;
        set { if (Set(ref _sharpness, Math.Round(value, 2))) { Raise(nameof(SharpnessText)); if (Running) _engine.SetSharpness(_sharpness); } }
    }
    public string SharpnessText => _sharpness.ToString("0.00", Inv);
    private string _capture = "wgc";
    public string Capture { get => _capture; set => Set(ref _capture, value); }
    public bool ShowCaptureOption => Platform.IsWindows;

    /// For the usage statistics: the multiplier in use, 0 while stopped.
    public int UsageMultiplier => !Running ? 0 : Mode == "auto" ? MaxMul : int.TryParse(Mode.TrimStart('x'), out var m) ? m : 0;

    private bool _running;
    public bool Running { get => _running; private set { if (Set(ref _running, value)) Raise(nameof(StartText)); } }
    public string StartText => Running ? "중지" : "시작";
    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }
    private string _gameInfo = "게임 창을 고르세요";
    public string GameInfo { get => _gameInfo; private set => Set(ref _gameInfo, value); }

    // live stats
    private string _srcFps = "—", _outFps = "—", _mult = "—", _latency = "—", _gen = "—", _analyze = "—", _late = "—", _transfer = "—";
    public string SrcFps { get => _srcFps; private set => Set(ref _srcFps, value); }
    public string OutFps { get => _outFps; private set => Set(ref _outFps, value); }
    public string Multiplier { get => _mult; private set => Set(ref _mult, value); }
    public string Latency { get => _latency; private set => Set(ref _latency, value); }
    public string GenMs { get => _gen; private set => Set(ref _gen, value); }
    public string AnalyzeMs { get => _analyze; private set => Set(ref _analyze, value); }
    public string Late { get => _late; private set => Set(ref _late, value); }
    public string Transfer { get => _transfer; private set => Set(ref _transfer, value); }
    private string _autoReason = "";
    public string AutoReason { get => _autoReason; private set { if (Set(ref _autoReason, value)) Raise(nameof(HasAutoReason)); } }
    public bool HasAutoReason => _autoReason.Length > 0;

    /// Auto shows a new image every `divider` refreshes, so the gaps between images stay equal.
    /// Empty when every refresh gets one: the output simply matches the monitor.
    public static string AutoReasonText(double refreshHz, int divider)
    {
        if (divider < 2 || refreshHz <= 0) return "";
        double hz = Math.Round(refreshHz);   // 143.97 reads as 144
        string part = divider == 2 ? "절반" : $"{divider}분의 1";
        return $"화면 간격이 고르게 {hz.ToString("0", Inv)}Hz의 {part}({(hz / divider).ToString("0.#", Inv)}fps)에 맞췄어요";
    }

    /// A fixed multiplier stops at the monitor: 4x of a 60 fps game is 120 fps on a 120 Hz monitor, so it reads 2.0.
    public static string CapReasonText(double refreshHz, double srcFps, int mode)
    {
        if (mode < 2 || refreshHz <= 0 || srcFps * mode <= refreshHz * 1.05) return "";
        double hz = Math.Round(refreshHz);
        return $"모니터가 {hz.ToString("0", Inv)}Hz라 출력은 {hz.ToString("0", Inv)}fps까지 나와요. {mode}배를 골라도 {(hz / srcFps).ToString("0.0", Inv)}배로 보여요";
    }

    /// Last ~60 samples for the chart (src, out).
    public List<(double src, double outp)> History { get; } = new();
    public event Action? HistoryChanged;

    // Session figures are differences of the engine's running totals, so a late or lost
    // stats line changes nothing.
    private DateTime _sessionStart;
    private FrameGenStats? _first, _last;
    private double _latencyMsSec;   // added latency x active seconds

    // The engine goes quiet while the game is behind another window. Without this the last numbers
    // would stay up, and a multiplier picked here would look like it did nothing.
    private readonly DispatcherTimer _statsWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastStats;
    private const long StatsGapMs = 2500;   // a stats line comes every 500 ms

    private void ClearStaleStats()
    {
        if (Environment.TickCount64 - _lastStats < StatsGapMs) return;
        SrcFps = OutFps = Multiplier = Latency = GenMs = AnalyzeMs = Late = Transfer = "—";
        AutoReason = "프레임 생성과 측정은 게임 창이 앞에 있을 때만 돌아가요. 게임으로 돌아가면 이어져요";
    }

    public FrameGenViewModel()
    {
        _engine.Stats += OnStats;
        _statsWatch.Tick += (_, _) => ClearStaleStats();
        _engine.Ready += r =>
        {
            string cap = r.TryGetProperty("capture", out var c) ? c.GetString() ?? "" : "";
            string gpu = r.TryGetProperty("genGpu", out var g) ? g.GetString() ?? "" : "";
            double hz = r.TryGetProperty("refreshHz", out var h) ? h.GetDouble() : 0;
            Status = $"실행 중 · 캡처 {cap} · 생성 GPU {gpu} · {hz:0}Hz";
        };
        _engine.Message += (type, msg) => Status = type == "error" ? "오류: " + Friendly(msg) : "알림: " + msg;
        _engine.Exited += () =>
        {
            EndSession();
            _statsWatch.Stop();
            Running = false;
            AutoReason = "";
            ResourceManagers.GameProcess = null;
            if (!Status.StartsWith("오류")) Status = "중지됨";
            if (_restart) { _restart = false; Toggle(); }
        };
    }

    public async Task RefreshAsync()
    {
        if (!Platform.EngineAvailable) { Status = $"엔진 파일이 없어요: {Platform.EnginePath}"; return; }
        var (list, err) = await FrameGenEngine.ListWindowsAsync();
        Windows.Clear();
        foreach (var w in list) Windows.Add(w);
        if (err != null) Status = "오류: " + Friendly(err);
        if (SelectedWindow == null && Windows.Count > 0) SelectedWindow = Windows[0];
        if (Gpus.Count == 0)
        {
            foreach (var g in await FrameGenEngine.ListGpusAsync()) Gpus.Add(g);
            if (Platform.IsWindows) Gpus.Insert(1, new GpuItem("igpu", "내장 그래픽 자동 선택", true));
            SelectedGpu = Gpus[0];
        }
    }

    public void Notice(string text) => Status = text;

    // The engine reports in English; every Mac user meets this one on the first run.
    private static string Friendly(string msg) => msg.StartsWith("screen recording permission")
        ? "화면 기록 권한이 필요해요. 시스템 설정 → 개인정보 보호 및 보안 → 화면 기록에서 SystemBoost를 켠 뒤 앱을 다시 열어 주세요."
        : msg;

    /// For settings that only count at start. Running stays true until Exited is handled, so the
    /// new start has to wait for that.
    public void Restart()
    {
        if (!Running) { Toggle(); return; }
        _restart = true;
        _engine.Stop();
    }
    private bool _restart;

    public void Toggle()
    {
        if (Running) { _engine.Stop(); return; }
        if (SelectedWindow == null) { Status = "게임 창을 먼저 고르세요"; return; }
        var o = new FrameGenOptions(Mode, MaxMul, Quality, Upscale, Sharpness, SelectedGpu?.Value ?? "same", Capture);
        try
        {
            _engine.Start(SelectedWindow.Id, o);
            ResourceManagers.GameProcess = ResourceManagers.ProcessOfWindow(SelectedWindow);
            Running = true;
            _lastStats = Environment.TickCount64;
            _statsWatch.Start();
            Status = "시작하는 중…";
            GameInfo = SelectedWindow.ToString();
            _sessionStart = DateTime.Now; _first = _last = null; _latencyMsSec = 0;
        }
        catch (Exception e) { Status = "오류: " + e.Message; }
    }

    private void OnStats(FrameGenStats s)
    {
        _lastStats = Environment.TickCount64;
        SrcFps = s.SrcFps.ToString("0", Inv);
        OutFps = s.OutFps.ToString("0", Inv);
        Multiplier = s.Multiplier.ToString("0.00", Inv);
        Latency = s.AddedLatencyMs.ToString("0.0", Inv);
        GenMs = s.GenMs.ToString("0.0", Inv);
        AnalyzeMs = s.AnalyzeMs.ToString("0.0", Inv);
        Late = s.Late.ToString(Inv);
        Transfer = s.TransferMs > 0 ? s.TransferMs.ToString("0.0", Inv) : "—";
        History.Add((s.SrcFps, s.OutFps));
        if (History.Count > 60) History.RemoveAt(0);
        HistoryChanged?.Invoke();
        AutoReason = s.Mode == 0 ? AutoReasonText(s.RefreshHz, s.Divider) : CapReasonText(s.RefreshHz, s.SrcFps, s.Mode);
        _first ??= s;
        if (_last != null) _latencyMsSec += s.AddedLatencyMs * Math.Max(0, s.ActiveSec - _last.ActiveSec);
        _last = s;
    }

    private void EndSession()
    {
        if (_first is not { } a || _last is not { } b || SelectedWindow == null) return;
        _first = _last = null;
        double sec = b.ActiveSec - a.ActiveSec;
        if (sec <= 0) return;
        Sessions.Insert(0, new SessionRecord(_sessionStart, SelectedWindow.Title, Mode, (b.SrcFrames - a.SrcFrames) / sec,
                                             (b.OutFrames - a.OutFrames) / sec, _latencyMsSec / sec, b.Late));
        SaveSessions();
    }

    public void Dispose()
    {
        _statsWatch.Stop();
        _engine.Dispose();
    }
}
