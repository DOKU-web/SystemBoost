// PC 맞춤 추천 (Ultra and Biz, Windows): start the check, see what it read and measured and what it recommends, apply the
// chosen recommendations at once and put them back. The check itself is PcCheckRunner (docs/pc-check/CONTRACT.md); this
// page only shows it, and a runner error is a line here, never a crash. Everything stays on this PC.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SystemBoost.App.Services;
using SystemBoost.App.Services.PcCheck;
using SystemBoost.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;

namespace SystemBoost.App.Pages;

[SupportedOSPlatform("windows")]
public sealed class PcCheckPage : UserControl
{
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF7A1A"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8B8A88"));
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly WindowsBoostBackend _backend;
    private readonly Window _owner;
    private readonly PcCheckRunner? _runner;
    private readonly string _noRunner = "";   // why there is no runner, when it could not be made

    // the check
    private readonly CheckBox _testPower = new()
    {
        Content = Ui.Text("전력 제한도 시험하기 (NVIDIA 데스크톱, 관리자 권한과 오버클럭 안내 동의가 필요해요)", "label", wrap: true),
    };
    private readonly Button _start = Ui.Button("검사 시작", "primary");
    private readonly Button _cancel = Ui.Button("취소", "ghost");
    private readonly TextBlock _progress = Ui.Text("", "label", wrap: true);
    private readonly TextBlock _status = Ui.Text("", "muted", wrap: true);
    private CancellationTokenSource? _cts;
    private bool _running, _busy;

    // the report: what it read and measured (rebuilt), the recommendations and what applying them did
    private readonly StackPanel _report = new() { Spacing = 16 };
    private readonly StackPanel _recList = new();
    private readonly List<(Recommendation rec, CheckBox box)> _choices = new();
    private readonly Button _apply = Ui.Button("선택한 추천 한 번에 적용", "primary");
    private readonly StackPanel _applied = new();
    private readonly TextBlock _actionStatus = Ui.Text("", "label", wrap: true);
    private readonly TextBlock _keepText = Ui.Text("", "label", wrap: true);
    private readonly Button _keep = Ui.Button("유지", "primary");
    private readonly StackPanel _keepBox = new() { Spacing = 8, IsVisible = false };
    private readonly Button _undo = Ui.Button("추천 적용 되돌리기", "ghost");
    private readonly StackPanel _undoBox = new() { Spacing = 6, IsVisible = false };

    public PcCheckPage(WindowsBoostBackend backend, FrameGenViewModel fg, Window owner)
    {
        _backend = backend;
        _owner = owner;
        try { _runner = new PcCheckRunner(backend, fg); }
        catch (Exception e) { _status.Text = _noRunner = $"검사를 준비하지 못했어요: {e.Message}"; }

        var root = new StackPanel { Spacing = 16 };
        var header = Ui.Header("PC 맞춤 추천", "이 PC의 사양을 읽고 CPU와 프레임 생성을 짧게 측정해서, 이 PC에 맞는 설정을 추천해요. "
                                             + "고른 추천만 한 번에 적용하고, 다시 한 번에 되돌릴 수 있어요.");
        header.Margin = new Thickness(0, 0, 0, 4);
        root.Children.Add(header);
        root.Children.Add(Ui.Card(StartCard()));
        root.Children.Add(_report);
        root.Children.Add(Ui.Card(RecommendationCard()));
        Content = new ScrollViewer { Content = root };

        PcCheckReport? last = null;
        try { last = PcCheckRunner.LastReport; }
        catch (Exception e) { _actionStatus.Text = $"지난 검사 결과를 읽지 못했어요: {e.Message}"; }
        Show(last, fresh: false);

        // the '유지' countdown of a GPU change runs on the backend's clock: shown while the page is on screen
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshKeep();
        AttachedToVisualTree += (_, _) => { RefreshKeep(); RefreshUndo(); timer.Start(); };
        DetachedFromVisualTree += (_, _) => timer.Stop();
        backend.Changed += () => Dispatcher.UIThread.Post(() => { RefreshKeep(); RefreshUndo(); });
    }

    private static string PlanLine =>
        $"PC 맞춤 추천은 {License.PlanOf(PremiumFeature.PcCheck)} 기능이에요. 왼쪽 '★ 플랜'에서 열 수 있어요.";

    // ------------------------------------------------------------ the check
    private Control StartCard()
    {
        var p = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text("검사", "h2"));
        head.Children.Add(Ui.Badge(License.PlanOf(PremiumFeature.PcCheck)));
        p.Children.Add(head);
        p.Children.Add(Ui.Text("약 1분 걸려요. 게임을 끈 뒤에 시작해 주세요. 검사하는 동안 CPU와 그래픽카드를 잠깐 바쁘게 써요. "
                               + "검사 결과는 이 PC에만 저장되고 어디로도 보내지 않아요.", "muted", wrap: true));
        p.Children.Add(_testPower);
        p.Children.Add(Ui.Text("전력 제한 시험은 같은 부하를 기본값과 카드가 허용하는 최대값에서 돌려 비교하고, 끝나면 원래 값으로 돌려요. "
                               + "클럭은 바꾸지 않아요. 시험하면 검사가 조금 더 걸려요.", "muted", wrap: true));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(_start);
        buttons.Children.Add(_cancel);
        _cancel.IsVisible = false;
        p.Children.Add(buttons);
        p.Children.Add(_progress);
        p.Children.Add(_status);
        _progress.IsVisible = false;
        _start.Click += (_, _) => Start();
        _cancel.Click += (_, _) =>
        {
            _cancel.IsEnabled = false;
            _progress.Text = "취소하는 중…";
            try { _cts?.Cancel(); }
            catch (Exception) { }   // already finished
        };
        return p;
    }

    private async void Start()
    {
        if (_busy) return;
        if (!License.Allowed(PremiumFeature.PcCheck)) { _status.Text = PlanLine; return; }   // like 게임별 측정
        if (_runner == null) { _status.Text = _noRunner; return; }
        string? why;
        try { why = _runner.CannotStart(); }
        catch (Exception e) { _status.Text = $"검사를 시작하지 못했어요: {e.Message}"; return; }
        if (why != null) { _status.Text = why; return; }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _running = true;
        SetBusy(true);
        _status.Text = "";
        _progress.Text = "검사하는 중…";
        _progress.IsVisible = true;
        try
        {
            var report = await _runner.RunAsync(_owner, _testPower.IsChecked == true,
                line => Dispatcher.UIThread.Post(() => { if (_running && !cts.IsCancellationRequested) _progress.Text = line; }), cts.Token);
            _applied.Children.Clear();
            _actionStatus.Text = "";
            Show(report, fresh: true);
            _status.Text = "검사를 마쳤어요. 아래 추천을 확인해 주세요.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _status.Text = "검사를 취소했어요. 검사 중에 바꾼 설정은 원래대로 돌려요.";
        }
        catch (Exception e) { _status.Text = $"검사하지 못했어요: {e.Message}"; }
        finally
        {
            _running = false;
            _cts = null;
            cts.Dispose();
            _progress.Text = "";
            _progress.IsVisible = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _start.IsEnabled = _testPower.IsEnabled = _apply.IsEnabled = _undo.IsEnabled = !busy;
        _cancel.IsVisible = _running;
        _cancel.IsEnabled = true;
        foreach (var (_, box) in _choices) box.IsEnabled = !busy;
    }

    // ------------------------------------------------------------ the report
    private void Show(PcCheckReport? r, bool fresh)
    {
        _report.Children.Clear();
        _recList.Children.Clear();
        _choices.Clear();
        try
        {
            if (r == null)
            {
                _recList.Children.Add(Ui.Text("아직 검사한 기록이 없어요. '검사 시작'을 누르면 결과가 여기에 보여요.", "muted", wrap: true));
            }
            else
            {
                _report.Children.Add(Ui.Card(SpecsCard(r, fresh)));
                if (BenchRows(r.Bench) is { Count: > 0 } rows)
                {
                    var bench = new StackPanel { Spacing = 10 };
                    bench.Children.Add(Ui.Text("측정 결과", "h2"));
                    bench.Children.Add(Facts(rows));
                    _report.Children.Add(Ui.Card(bench));
                }
                FillRecommendations(r.Recommendations ?? Array.Empty<Recommendation>());
            }
        }
        catch (Exception e)   // a report saved by another version, or a part missing: a line instead of the page
        {
            _report.Children.Clear();
            _recList.Children.Clear();
            _choices.Clear();
            _recList.Children.Add(Ui.Text($"검사 결과를 보여 주지 못했어요: {e.Message}", "muted", wrap: true));
        }
        _report.IsVisible = _report.Children.Count > 0;
        _apply.IsVisible = _choices.Count > 0;
        SetBusy(_busy);
        RefreshUndo();
        RefreshKeep();
    }

    private static Control SpecsCard(PcCheckReport r, bool fresh)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text(fresh ? "검사 결과" : "지난 검사 결과", "h2"));
        var at = (r.At.Kind == DateTimeKind.Utc ? r.At.ToLocalTime() : r.At).ToString("yyyy-MM-dd HH:mm", Inv);
        var version = (r.AppVersion ?? "").Split('+')[0];
        p.Children.Add(Ui.Text(version.Length > 0 ? $"{at}에 검사했어요 · SystemBoost {version}" : $"{at}에 검사했어요", "label"));
        p.Children.Add(Ui.Text("부품, 드라이버나 화면 설정을 바꿨으면 다시 검사해 주세요.", "muted", wrap: true));
        if (r.Specs is { } s) p.Children.Add(Facts(SpecRows(s)));
        return p;
    }

    private static List<(string label, List<TextBlock> lines)> SpecRows(PcSpecs s)
    {
        var rows = new List<(string, List<TextBlock>)>();

        var cpu = new List<TextBlock> { Line(s.CpuName ?? "") };
        if (s.Cores > 0)
            cpu.Add(Line(s.ECores > 0 ? $"코어 {s.Cores}개 · 스레드 {s.Threads}개 · P코어 {s.PCores}개 · E코어 {s.ECores}개"
                                      : $"코어 {s.Cores}개 · 스레드 {s.Threads}개", muted: true));
        rows.Add(("프로세서", cpu));

        var gb = Gb(s.RamMB);
        var mem = new List<TextBlock>
        {
            Line(s.RamConfiguredMTs <= 0 ? $"{gb} GB"
                 : s.RamRatedMTs > s.RamConfiguredMTs ? $"{gb} GB · {s.RamConfiguredMTs} MT/s (정격 {s.RamRatedMTs} MT/s)"
                 : $"{gb} GB · {s.RamConfiguredMTs} MT/s"),
        };
        if (s.RamSticks > 0) mem.Add(Line($"메모리 모듈 {s.RamSticks}개", muted: true));
        rows.Add(("메모리", mem));

        var gpus = (s.Gpus ?? Array.Empty<GpuSpec>())
            .Select(g => Line(g.Integrated ? $"{g.Name} (내장)" : g.VramMB > 0 ? $"{g.Name} · VRAM {Gb(g.VramMB)} GB" : g.Name))
            .ToList();
        if (gpus.Count == 0) gpus.Add(Line("그래픽카드를 읽지 못했어요", muted: true));
        rows.Add(("그래픽카드", gpus));

        if (s.Display is { Width: > 0 } d)
        {
            string hz = d.RefreshHz.ToString("0.##", Inv), max = d.MaxRefreshHz.ToString("0.##", Inv);
            rows.Add(("화면", new List<TextBlock>
            {
                Line(d.MaxRefreshHz > d.RefreshHz + 0.5 ? $"{d.Width}×{d.Height} · {hz} Hz · 최대 {max} Hz" : $"{d.Width}×{d.Height} · {hz} Hz"),
            }));
        }

        rows.Add(("PC 종류", new List<TextBlock>
        {
            Line(!s.Laptop ? "데스크톱" : s.OnBattery ? "노트북 · 배터리로 쓰는 중" : "노트북 · 전원 연결됨"),
        }));
        return rows;
    }

    private static List<(string label, List<TextBlock> lines)> BenchRows(BenchResults? b)
    {
        var rows = new List<(string, List<TextBlock>)>();
        if (b == null) return rows;

        var cpu = new List<TextBlock>();
        if (b.Cpu is { } c)
        {
            cpu.Add(Line($"단일 스레드 {Score(c.SingleScore)} · 멀티 스레드 {Score(c.MultiScore)} · 오차 {Pct(c.NoisePercent)}%"));
            if (c.HighPlanMultiScore is double high)
                cpu.Add(Line(c.HighPlanNoisePercent is double noise
                    ? $"고성능 전원 계획에서 멀티 스레드 {Score(high)} · 오차 {Pct(noise)}%"
                    : $"고성능 전원 계획에서 멀티 스레드 {Score(high)}"));
            cpu.Add(Line("점수는 이 앱의 단위예요. 높을수록 빨라요.", muted: true));
        }
        if (b.CpuError is { Length: > 0 } cpuError) cpu.Add(Line($"측정하지 못했어요: {cpuError}", muted: true));
        if (cpu.Count > 0) rows.Add(("CPU", cpu));

        var fg = new List<TextBlock>();
        var benches = b.FrameGen ?? Array.Empty<FgBench>();
        if (benches.Count > 0)
        {
            var f0 = benches[0];
            fg.Add(Line(f0.Gpu is { Length: > 0 } gpu ? $"{f0.Width}×{f0.Height} · {gpu}" : $"{f0.Width}×{f0.Height}"));
            foreach (var f in benches)
                fg.Add(Line($"{QualityName(f.Quality)}: 분석 {Ms(f.AnalyzeMs)} ms · 생성 {Ms(f.GenMs)} ms"));
            fg.Add(Line("게임 없이 합성 화면으로 잰 GPU 시간이에요. 분석은 게임 프레임마다, 생성은 만든 프레임마다 걸려요.", muted: true));
        }
        if (b.FgError is { Length: > 0 } fgError) fg.Add(Line($"측정하지 못했어요: {fgError}", muted: true));
        if (fg.Count > 0) rows.Add(("프레임 생성", fg));

        var power = new List<TextBlock>();
        if (b.Power is { } p)
        {
            if (p.Gpu is { Length: > 0 }) power.Add(Line(p.Gpu));
            power.Add(Line($"{p.DefaultW} W (기본): {Fps(p.DefaultFps)} fps → {p.TestedW} W: {Fps(p.TestedFps)} fps"));
            power.Add(Line($"오차 {Pct(p.NoisePercent)}% · 최고 {p.MaxTempC} °C", muted: true));
            power.Add(Line("같은 프레임 생성 부하를 돌려 잰 초당 프레임이에요. 게임 FPS가 아니에요.", muted: true));
            if (p.Fault is { Length: > 0 } fault) power.Add(Line($"중간에 멈췄어요 ({fault}). 그래픽카드는 원래 값으로 돌려 놓았어요.", muted: true));
        }
        if (b.PowerError is { Length: > 0 } powerError) power.Add(Line($"시험하지 못했어요: {powerError}", muted: true));
        if (power.Count > 0) rows.Add(("전력 제한", power));
        return rows;
    }

    private static string Gb(long mb) => (mb / 1024.0).ToString("0.#", Inv);
    private static string Score(double v) => v.ToString("N0", Inv);
    private static string Pct(double v) => v.ToString("0.0", Inv);
    private static string Ms(double v) => v.ToString("0.00", Inv);
    private static string Fps(double v) => v.ToString("0.0", Inv);

    private static string QualityName(string quality) => quality switch
    {
        "low" => "저사양", "performance" => "성능", "balanced" => "균형", "quality" => "품질", _ => quality,
    };

    private static TextBlock Line(string text, bool muted = false) => Ui.Text(text, muted ? "muted" : "label", wrap: true);

    // label on the left, its lines on the right
    private static Grid Facts(List<(string label, List<TextBlock> lines)> rows)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*") };
        for (int i = 0; i < rows.Count; i++)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Ui.Text(rows[i].label, "sub");
            label.Margin = new Thickness(0, 6, 12, 6);
            var lines = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6) };
            foreach (var line in rows[i].lines) lines.Children.Add(line);
            Grid.SetRow(label, i);
            Grid.SetRow(lines, i);
            Grid.SetColumn(lines, 1);
            g.Children.Add(label);
            g.Children.Add(lines);
        }
        return g;
    }

    // ------------------------------------------------------------ recommendations, apply, keep, undo
    private Control RecommendationCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("추천", "h2"));
        p.Children.Add(Ui.Text("체크한 추천만 적용해요. 여기서 적용한 것은 아래 '추천 적용 되돌리기'로 한 번에 원래대로 돌릴 수 있어요. "
                               + "'참고'는 앱이 바꾸지 않아요.", "muted", wrap: true));
        p.Children.Add(_recList);
        _apply.HorizontalAlignment = HorizontalAlignment.Left;
        _apply.Margin = new Thickness(0, 6, 0, 0);
        _apply.Click += (_, _) => Apply();
        p.Children.Add(_apply);
        p.Children.Add(_actionStatus);
        p.Children.Add(_applied);

        // after a GPU item: the helper puts the card back unless '유지' is pressed in time (as on the GPU page)
        _keep.HorizontalAlignment = HorizontalAlignment.Left;
        _keep.Click += async (_, _) =>
        {
            _keep.IsEnabled = false;
            try { _actionStatus.Text = "오버클럭 / 전력 제한: " + await _backend.KeepGpuAsync(); }
            catch (Exception e) { _actionStatus.Text = "오버클럭 / 전력 제한: " + e.Message; }
            finally { _keep.IsEnabled = true; RefreshKeep(); }
        };
        _keepBox.Children.Add(_keepText);
        _keepBox.Children.Add(_keep);
        p.Children.Add(_keepBox);

        _undo.HorizontalAlignment = HorizontalAlignment.Left;
        _undo.Click += (_, _) => Undo();
        _undoBox.Children.Add(_undo);
        _undoBox.Children.Add(Ui.Text("이 페이지에서 한 번에 적용한 것만 원래대로 돌려요. 앱을 다시 켠 뒤에도 돌릴 수 있어요.", "muted", wrap: true));
        p.Children.Add(_undoBox);
        _actionStatus.IsVisible = false;
        _actionStatus.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) _actionStatus.IsVisible = !string.IsNullOrEmpty(_actionStatus.Text);
        };
        return p;
    }

    private void FillRecommendations(IReadOnlyList<Recommendation> recs)
    {
        if (recs.Count == 0)
        {
            _recList.Children.Add(Ui.Text("지금 바꿀 추천이 없어요.", "muted", wrap: true));
            return;
        }
        RecKind? kind = null;
        bool first = true;
        foreach (var rec in recs)
        {
            if (rec.Kind != kind)   // the advisor's order: tips, frame generation, boost items, GPU power
            {
                var head = Ui.Text(KindTitle(rec.Kind), "label accent");
                head.FontWeight = FontWeight.SemiBold;
                head.Margin = new Thickness(0, kind == null ? 0 : 14, 0, 2);
                _recList.Children.Add(head);
                kind = rec.Kind;
                first = true;
            }
            _recList.Children.Add(RecRow(rec, first));
            first = false;
        }
    }

    private static string KindTitle(RecKind kind) => kind switch
    {
        RecKind.Tip => "직접 확인할 것",
        RecKind.FrameGen => "프레임 생성",
        RecKind.Boost => "부스트 항목",
        _ => "GPU 전력 제한",
    };

    private Border RecRow(Recommendation rec, bool first)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*") };
        if (rec.Applicable)
        {
            var box = new CheckBox
            {
                IsChecked = rec.SelectedByDefault, MinWidth = 0, Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top, IsEnabled = !_busy,
            };
            Avalonia.Automation.AutomationProperties.SetName(box, rec.Title);
            g.Children.Add(box);
            _choices.Add((rec, box));
        }

        var text = new StackPanel { Spacing = 3, Margin = new Thickness(0, 5, 0, 0) };
        var title = new WrapPanel { Orientation = Orientation.Horizontal };
        var name = Ui.Text(rec.Title, "label");
        name.FontWeight = FontWeight.SemiBold;
        name.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(name);
        if (!rec.Applicable)
        {
            var tag = Ui.Tag("참고");
            tag.Margin = new Thickness(8, 0, 0, 0);
            title.Children.Add(tag);
        }
        text.Children.Add(title);
        if (Change(rec) is { } change) text.Children.Add(change);
        if (!string.IsNullOrEmpty(rec.Reason)) text.Children.Add(Ui.Text(rec.Reason, "muted", wrap: true));
        if (rec.Kind == RecKind.Gpu && rec.Applicable)
            text.Children.Add(Ui.Text("적용하기 전에 오버클럭 안내를 다시 보여 줘요. 적용한 뒤 '유지'를 누르지 않으면 원래 값으로 돌아가요.", "muted", wrap: true));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var row = new Border { Child = g };
        row.Classes.Add("row");
        if (first) row.BorderThickness = new Thickness(0);
        return row;
    }

    // "current → proposed", the proposal in the accent; each part is translated on its own (Lang hooks Run.Text)
    private static TextBlock? Change(Recommendation rec)
    {
        bool now = !string.IsNullOrEmpty(rec.Current), next = !string.IsNullOrEmpty(rec.Proposed);
        if (!now && !next) return null;
        var inlines = new InlineCollection();
        if (now) inlines.Add(new Run(rec.Current) { Foreground = Muted });
        if (now && next) inlines.Add(new Run("  →  ") { Foreground = Muted });
        if (next) inlines.Add(new Run(rec.Proposed) { Foreground = Orange, FontWeight = FontWeight.SemiBold });
        return new TextBlock { Inlines = inlines, FontSize = 14, TextWrapping = TextWrapping.Wrap };
    }

    private async void Apply()
    {
        if (_busy || _runner == null) return;
        if (!License.Allowed(PremiumFeature.PcCheck)) { _actionStatus.Text = PlanLine; return; }
        var chosen = _choices.Where(c => c.box.IsChecked == true).Select(c => c.rec).ToList();
        if (chosen.Count == 0) { _actionStatus.Text = "선택한 추천이 없어요. 적용할 추천을 체크해 주세요."; return; }
        SetBusy(true);
        _applied.Children.Clear();
        _actionStatus.Text = "적용하는 중…";
        try
        {
            var results = await _runner.ApplyAsync(_owner, chosen);
            int ok = results.Count(x => x.Ok), failed = results.Count - ok;
            _actionStatus.Text = results.Count == 0 ? "적용한 추천이 없어요."
                               : failed == 0 ? $"추천 {ok}개를 적용했어요."
                               : $"추천 {ok}개를 적용했고 {failed}개는 적용하지 못했어요.";
            bool first = true;
            foreach (var res in results)
            {
                var title = chosen.FirstOrDefault(c => c.Id == res.Id)?.Title ?? res.Id;
                _applied.Children.Add(Ui.Row(title, res.Text, res.Ok ? Ui.Tag("적용했어요") : Ui.Badge("안 됨"), first));
                first = false;
            }
        }
        catch (Exception e) { _actionStatus.Text = $"적용하지 못했어요: {e.Message}"; }
        finally
        {
            SetBusy(false);
            RefreshUndo();
            RefreshKeep();
        }
    }

    private async void Undo()
    {
        if (_busy || _runner == null) return;
        SetBusy(true);
        _actionStatus.Text = "되돌리는 중…";
        try
        {
            _actionStatus.Text = await _runner.UndoAsync();
            _applied.Children.Clear();
        }
        catch (Exception e) { _actionStatus.Text = $"되돌리지 못했어요: {e.Message}"; }
        finally
        {
            SetBusy(false);
            RefreshUndo();
            RefreshKeep();
        }
    }

    private void RefreshUndo()
    {
        bool can = false;
        try { can = _runner?.CanUndo == true; }
        catch (Exception) { }
        _undoBox.IsVisible = can;
    }

    private void RefreshKeep()
    {
        int? left = null;
        try { left = _backend.GpuConfirmLeft; }
        catch (Exception) { }
        _keepBox.IsVisible = left is > 0;
        if (left is int s and > 0)
            _keepText.Text = $"화면과 게임이 정상이면 {s}초 안에 '유지'를 눌러 주세요. 누르지 않으면 원래대로 돌아가요.";
    }

    // ------------------------------------------------------------ dashboard
    /// The dashboard's card: what the check does, and the way to this page.
    public static Control Tile(Action<string> go)
    {
        var p = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text("PC 맞춤 추천", "h2"));
        head.Children.Add(Ui.Badge(License.PlanOf(PremiumFeature.PcCheck)));
        p.Children.Add(head);
        p.Children.Add(Ui.Text("사양을 읽고 짧게 측정해서 이 PC에 맞는 프레임 생성, 부스트 항목, 전력 제한을 추천해요. 고른 것만 한 번에 적용해요.", "sub", wrap: true));
        var open = Ui.Button("열기", "primary");
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Click += (_, _) => go("pccheck");
        p.Children.Add(open);
        return p;
    }
}
