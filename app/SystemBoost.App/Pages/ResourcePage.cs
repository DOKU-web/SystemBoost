// "자원 분배": per-app CPU / GPU / RAM usage as a stacked graph + who gets more.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SystemBoost.App.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SystemBoost.App.Pages;

/// Stacked area chart: one band per app (top N), newest sample on the right.
public sealed class StackedChart : Control
{
    public static readonly Color[] Palette =
    {
        Color.Parse("#FF7A1A"), Color.Parse("#5AA7FF"), Color.Parse("#5BE38A"), Color.Parse("#C78BFF"),
        Color.Parse("#FFD24D"), Color.Parse("#FF6B8B"), Color.Parse("#5D5C60"),
    };
    private List<(string name, double[] values)> _series = new();
    private double _max = 100;
    private string _unit = "%";

    public void SetData(List<(string name, double[] values)> series, double max, string unit)
    {
        _series = series; _max = Math.Max(1, max); _unit = unit;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (_series.Count == 0 || _series[0].values.Length < 2) { DrawGrid(ctx, b); return; }
        int n = _series[0].values.Length;
        var below = new double[n];
        for (int s = 0; s < _series.Count; s++)
        {
            var vals = _series[s].values;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                Point P(int i, double v) => new(b.Width * i / (n - 1), b.Height * (1 - Math.Min(1, v / _max)));
                g.BeginFigure(P(0, below[0] + vals[0]), true);
                for (int i = 1; i < n; i++) g.LineTo(P(i, below[i] + vals[i]));
                for (int i = n - 1; i >= 0; i--) g.LineTo(P(i, below[i]));
                g.EndFigure(true);
            }
            var c = Palette[Math.Min(s, Palette.Length - 1)];
            ctx.DrawGeometry(new SolidColorBrush(c, 0.75), null, geo);
            for (int i = 0; i < n; i++) below[i] += vals[i];
        }
        DrawGrid(ctx, b);
    }

    private void DrawGrid(DrawingContext ctx, Rect b)
    {
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1);
        var label = new SolidColorBrush(Color.Parse("#E8E6E3"));
        for (int i = 1; i <= 3; i++)
        {
            double y = b.Height * i / 4;
            ctx.DrawLine(grid, new Point(0, y), new Point(b.Width, y));
            ctx.DrawText(new FormattedText($"{_max * (1 - i / 4.0):0}{_unit}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                           new Typeface(FontFamily.Default), 11, label), new Point(6, y - 15));
        }
    }
}

public sealed class ResourcePage : UserControl
{
    private const int History = 60, TopN = 6;
    private readonly IResourceManager _rm;
    private readonly StackedChart _chart = new() { Height = 220 };
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = Ui.Text("", "muted", wrap: true);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, Queue<(double cpu, double gpu, double ram)>> _hist = new();
    private readonly Dictionary<string, ShareLevel> _levels = new();
    private readonly Dictionary<string, int> _caps = new();
    private List<AppSample> _last = new();
    private readonly Dictionary<string, TextBlock> _usageText = new();
    private string _rowKey = "";
    private string _metric = "cpu";

    public ResourcePage(IResourceManager rm)
    {
        _rm = rm;
        var root = new StackPanel();
        root.Children.Add(Ui.Header("자원 분배", "앱마다 CPU·GPU·메모리를 얼마나 쓰는지 보고, 어떤 앱에 더 줄지 정해요. " +
            "게임 자체는 건드리지 않고, 다른 앱을 낮춰서 게임 몫을 늘려요."));

        // metric tabs
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        var segBox = new Border { Child = tabs, HorizontalAlignment = HorizontalAlignment.Left };
        segBox.Classes.Add("segbox");
        foreach (var (key, text) in new[] { ("cpu", "CPU"), ("gpu", "GPU"), ("ram", "메모리") })
        {
            var t = new ToggleButton { Content = text, Tag = key, IsChecked = key == _metric };
            t.Classes.Add("seg");
            t.Click += (_, _) =>
            {
                _metric = key;
                foreach (var o in tabs.Children.OfType<ToggleButton>()) o.IsChecked = (string)o.Tag! == key;
                Redraw();
            };
            tabs.Children.Add(t);
        }
        var chartCard = new StackPanel { Spacing = 12 };
        chartCard.Children.Add(segBox);
        chartCard.Children.Add(new Border { Child = _chart, Background = new SolidColorBrush(Color.Parse("#101012")), CornerRadius = new CornerRadius(12), ClipToBounds = true });
        chartCard.Children.Add(_legend);
        root.Children.Add(Ui.Card(chartCard));

        // per-app controls
        var listCard = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(Ui.Text("앱별 분배", "h2"));
        var reset = Ui.Button("모두 원래대로", "ghost");
        reset.Click += async (_, _) =>
        {
            await _rm.RestoreAllAsync();
            _levels.Clear();
            _status.Text = "모든 앱을 원래대로 돌렸어요";
            Redraw();
        };
        Grid.SetColumn(reset, 1);
        head.Children.Add(reset);
        listCard.Children.Add(head);
        listCard.Children.Add(Ui.Text(Platform.IsWindows
            ? "우선: CPU 우선순위를 조금 올려요 · 낮춤: 우선순위·메모리 우선순위를 낮추고 효율 모드, 다음 실행부터 내장 GPU · 제한: CPU 최대 사용량을 고정해요"
            : "macOS에서는 '낮춤'(백그라운드 모드)만 바꿀 수 있어요. GPU·메모리는 사용량만 보여 줘요. 다른 앱을 '우선'으로 올리는 것은 macOS가 허용하지 않아요.",
            "muted", wrap: true));
        listCard.Children.Add(Ui.Text("게임은 '보통'으로 두세요. 바꾼 설정은 SystemBoost를 끄면 원래대로 돌아가요.", "muted", wrap: true));
        listCard.Children.Add(_rows);
        listCard.Children.Add(_status);
        root.Children.Add(Ui.Card(listCard));

        Content = new ScrollViewer { Content = root };
        _timer.Tick += async (_, _) => await Tick();
        AttachedToVisualTree += async (_, _) => { _timer.Start(); await Tick(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private async System.Threading.Tasks.Task Tick()
    {
        _last = await _rm.SampleAsync();
        var seen = new HashSet<string>();
        foreach (var a in _last)
        {
            if (!_hist.TryGetValue(a.Name, out var q)) _hist[a.Name] = q = new Queue<(double, double, double)>();
            q.Enqueue((a.Cpu, Math.Max(0, a.Gpu), a.RamMB));
            while (q.Count > History) q.Dequeue();
            seen.Add(a.Name);
        }
        foreach (var k in _hist.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var q = _hist[k]; q.Enqueue((0, 0, 0));
            while (q.Count > History) q.Dequeue();
            if (q.All(v => v.cpu == 0 && v.gpu == 0 && v.ram == 0)) _hist.Remove(k);
        }
        Redraw();
    }

    private double Value((double cpu, double gpu, double ram) v) => _metric switch { "gpu" => v.gpu, "ram" => v.ram, _ => v.cpu };

    private void Redraw()
    {
        if (_metric == "gpu" && !_rm.MeasuresGpu)
        {
            _chart.SetData(new(), 100, "%");
            _legend.Children.Clear();
            _legend.Children.Add(Ui.Text(Platform.IsMac ? "macOS는 앱별 GPU 사용량을 공개하지 않아서 보여 줄 수 없어요" : "이 PC에서는 GPU 사용량을 읽을 수 없어요", "muted"));
            UpdateRows();
            return;
        }
        // top N by recent average, rest as "기타"
        var ranked = _hist.OrderByDescending(kv => kv.Value.TakeLast(5).Select(Value).DefaultIfEmpty(0).Average()).ToList();
        var top = ranked.Take(TopN).ToList();
        var series = new List<(string, double[])>();
        foreach (var kv in top) series.Add((kv.Key, Pad(kv.Value.Select(Value).ToArray())));
        var rest = ranked.Skip(TopN).Select(kv => Pad(kv.Value.Select(Value).ToArray())).ToList();
        if (rest.Count > 0) series.Add(("기타", Enumerable.Range(0, History).Select(i => rest.Sum(r => r[i])).ToArray()));

        double max = _metric switch
        {
            "ram" => Math.Max(1024, Enumerable.Range(0, History).Max(i => series.Sum(s => s.Item2[i])) * 1.15),
            _ => 100,
        };
        _chart.SetData(series, max, _metric == "ram" ? "MB" : "%");
        _legend.Children.Clear();
        for (int i = 0; i < series.Count; i++)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            p.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
                                        Background = new SolidColorBrush(StackedChart.Palette[Math.Min(i, StackedChart.Palette.Length - 1)]) });
            p.Children.Add(Ui.Text(series[i].Item1, "muted"));
            _legend.Children.Add(p);
        }
        UpdateRows();
    }

    private static string Usage(AppSample a) =>
        $"CPU {a.Cpu:0}%" + (a.Gpu >= 0 ? $" · GPU {a.Gpu:0}%" : "") + $" · 메모리 {a.RamMB:0} MB";

    private List<AppSample> RowApps() =>
        _last.OrderByDescending(a => a.Cpu * 10 + a.RamMB / 100 + Math.Max(0, a.Gpu) * 10).Take(12)
             .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();   // stable order while using the controls

    /// Rebuild rows only when the set of apps changes; otherwise just refresh the numbers.
    private void UpdateRows()
    {
        var apps = RowApps();
        var key = string.Join("|", apps.Select(a => a.Name));
        if (key != _rowKey) { _rowKey = key; BuildRows(); return; }
        foreach (var a in apps) if (_usageText.TryGetValue(a.Name, out var t)) t.Text = Usage(a);
    }

    private static double[] Pad(double[] v) =>
        v.Length >= History ? v[^History..] : Enumerable.Repeat(0.0, History - v.Length).Concat(v).ToArray();

    private void BuildRows()
    {
        _rows.Children.Clear();
        _usageText.Clear();
        var apps = RowApps();
        bool first = true;
        foreach (var a in apps)
        {
            var level = _levels.GetValueOrDefault(a.Name, ShareLevel.Normal);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            if (level == ShareLevel.Limit && _rm.CanCapCpu)
            {
                var cap = _caps.GetValueOrDefault(a.Name, 25);
                var lbl = Ui.Text($"CPU 최대 {cap}%", "label");
                lbl.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
                lbl.VerticalAlignment = VerticalAlignment.Center;
                var sl = new Slider { Minimum = 5, Maximum = 90, Value = cap, Width = 140, VerticalAlignment = VerticalAlignment.Center };
                sl.PropertyChanged += async (_, e) =>
                {
                    if (e.Property != RangeBase.ValueProperty) return;
                    int v = (int)Math.Round(sl.Value / 5) * 5;
                    if (v == _caps.GetValueOrDefault(a.Name, 25)) return;
                    _caps[a.Name] = v;
                    lbl.Text = $"CPU 최대 {v}%";
                    _status.Text = await _rm.SetLevelAsync(a, ShareLevel.Limit, v);
                };
                right.Children.Add(sl);
                right.Children.Add(lbl);
            }
            var segP = new StackPanel { Orientation = Orientation.Horizontal };
            var seg = new Border { Child = segP };
            seg.Classes.Add("segbox");
            var options = new List<(ShareLevel, string)> { (ShareLevel.Normal, "보통"), (ShareLevel.Lower, "낮춤") };
            if (Platform.IsWindows) { options.Insert(0, (ShareLevel.Priority, "우선")); options.Add((ShareLevel.Limit, "제한")); }
            foreach (var (lv, text) in options)
            {
                var t = new ToggleButton { Content = text, IsChecked = lv == level };
                t.Classes.Add("seg");
                t.Click += async (_, _) =>
                {
                    if (lv == ShareLevel.Limit && !License.Allowed(PremiumFeature.CpuLimit))
                    {
                        _status.Text = "CPU 최대 사용량 제한은 프리미엄 기능이에요. 왼쪽 '★ 프리미엄'에서 열 수 있어요.";
                        BuildRows();
                        return;
                    }
                    _levels[a.Name] = lv;
                    _status.Text = await _rm.SetLevelAsync(a, lv, _caps.GetValueOrDefault(a.Name, 25));
                    BuildRows();
                };
                segP.Children.Add(t);
            }
            right.Children.Add(seg);
            var row = Ui.Row(a.Name, Usage(a), right, first);
            if (row.Child is Grid g && g.Children[0] is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock ut)
                _usageText[a.Name] = ut;
            _rows.Children.Add(row);
            first = false;
        }
    }
}
