using SystemBoost.App.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
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

    private string _mode = License.IsPremium ? "auto" : "x2";
    public string Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value) && Running) _engine.SetMode(value); }
    }
    private int _maxMul = License.IsPremium ? 4 : 2;
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

    /// Last ~60 samples for the chart (src, out).
    public List<(double src, double outp)> History { get; } = new();
    public event Action? HistoryChanged;

    // session accumulation
    private DateTime _sessionStart;
    private double _sumSrc, _sumOut, _sumLat; private int _n; private long _lastLate;

    public FrameGenViewModel()
    {
        _engine.Stats += OnStats;
        _engine.Ready += r =>
        {
            string cap = r.TryGetProperty("capture", out var c) ? c.GetString() ?? "" : "";
            string gpu = r.TryGetProperty("genGpu", out var g) ? g.GetString() ?? "" : "";
            double hz = r.TryGetProperty("refreshHz", out var h) ? h.GetDouble() : 0;
            Status = $"실행 중 · 캡처 {cap} · 생성 GPU {gpu} · {hz:0}Hz";
        };
        _engine.Message += (type, msg) => Status = (type == "error" ? "오류: " : "알림: ") + msg;
        _engine.Exited += () => { EndSession(); Running = false; if (!Status.StartsWith("오류")) Status = "중지됨"; };
    }

    public async Task RefreshAsync()
    {
        if (!Platform.EngineAvailable) { Status = $"엔진 파일이 없어요: {Platform.EnginePath}"; return; }
        var (list, err) = await FrameGenEngine.ListWindowsAsync();
        Windows.Clear();
        foreach (var w in list) Windows.Add(w);
        if (err != null) Status = "오류: " + err;
        if (SelectedWindow == null && Windows.Count > 0) SelectedWindow = Windows[0];
        if (Gpus.Count == 0)
        {
            foreach (var g in await FrameGenEngine.ListGpusAsync()) Gpus.Add(g);
            if (Platform.IsWindows) Gpus.Insert(1, new GpuItem("igpu", "내장 그래픽 자동 선택", true));
            SelectedGpu = Gpus[0];
        }
    }

    public void Notice(string text) => Status = text;

    public void Toggle()
    {
        if (Running) { _engine.Stop(); return; }
        if (SelectedWindow == null) { Status = "게임 창을 먼저 고르세요"; return; }
        var o = new FrameGenOptions(Mode, MaxMul, Quality, Upscale, Sharpness, SelectedGpu?.Value ?? "same", Capture);
        try
        {
            _engine.Start(SelectedWindow.Id, o);
            Running = true;
            Status = "시작하는 중…";
            GameInfo = SelectedWindow.ToString();
            _sessionStart = DateTime.Now; _sumSrc = _sumOut = _sumLat = 0; _n = 0; _lastLate = 0;
        }
        catch (Exception e) { Status = "오류: " + e.Message; }
    }

    private void OnStats(FrameGenStats s)
    {
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
        if (s.SrcFps > 0) { _sumSrc += s.SrcFps; _sumOut += s.OutFps; _sumLat += s.AddedLatencyMs; _n++; _lastLate = s.Late; }
    }

    private void EndSession()
    {
        if (_n == 0 || SelectedWindow == null) return;
        Sessions.Insert(0, new SessionRecord(_sessionStart, SelectedWindow.Title, Mode, _sumSrc / _n, _sumOut / _n, _sumLat / _n, _lastLate));
        _n = 0;
        SaveSessions();
    }

    public void Dispose() => _engine.Dispose();
}
