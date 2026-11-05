// "가상 메모리 (페이징 파일)": see the current setup, pick 자동 / 권장 / 직접, apply with one UAC prompt.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SystemBoost.App.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemBoost.App.Pages;

public sealed class VirtualMemoryCard : UserControl
{
    private readonly TextBlock _ram = Ui.Text("", "label");
    private readonly TextBlock _current = Ui.Text("", "label");
    private readonly ComboBox _drive = new();
    private readonly StackPanel _custom = new() { Spacing = 8, IsVisible = false };
    private readonly Slider _init = new() { Minimum = 1, Maximum = 64, Width = 260 };
    private readonly Slider _max = new() { Minimum = 1, Maximum = 128, Width = 260 };
    private readonly TextBlock _initT = Ui.Text("", "label"), _maxT = Ui.Text("", "label");
    private readonly TextBlock _plan = Ui.Text("", "label");
    private readonly TextBlock _msg = Ui.Text("", "muted", wrap: true);
    private readonly Button _restart = Ui.Button("지금 다시 시작", "ghost");
    private readonly Button _revert = Ui.Button("원래대로", "ghost");
    private string _choice = "recommend";
    private long _ramMB;

    public VirtualMemoryCard()
    {
        Margin = new Thickness(0, 20, 0, 0);
        _ramMB = VirtualMemory.InstalledRamMB();
        var p = new StackPanel { Spacing = 6 };
        p.Children.Add(Ui.Text("가상 메모리 (페이징 파일)", "h2"));
        p.Children.Add(Ui.Text("메모리가 모자랄 때 디스크 일부를 메모리처럼 써서 게임이 튕기거나 '메모리 부족' 오류가 나는 것을 막아요. " +
                               "램이 늘어나거나 FPS가 오르는 기능은 아니에요. 바꾸면 다시 시작한 뒤 적용돼요.", "sub", wrap: true));
        p.Children.Add(Ui.Row("설치된 메모리(RAM)", "", _ram, first: true));
        p.Children.Add(Ui.Row("지금 설정", "", _current));
        p.Children.Add(Ui.Row("둘 곳", "가장 빠른 SSD를 고르세요. 게임이 깔린 드라이브와 같아도 괜찮아요", _drive));

        var segP = new StackPanel { Orientation = Orientation.Horizontal };
        var seg = new Border { Child = segP };
        seg.Classes.Add("segbox");
        foreach (var (key, text) in new[] { ("auto", "자동 (Windows)"), ("recommend", "권장 (게임용)"), ("custom", "직접 입력") })
        {
            var t = new ToggleButton { Content = text, IsChecked = key == _choice };
            t.Classes.Add("seg");
            t.Click += (_, _) =>
            {
                _choice = key;
                foreach (var o in segP.Children.OfType<ToggleButton>()) o.IsChecked = o == t;
                _custom.IsVisible = key == "custom";
                UpdatePlan();
            };
            segP.Children.Add(t);
        }
        p.Children.Add(Ui.Row("크기", "권장: 메모리 크기만큼 시작, 최대 그 2배 (남은 공간에 맞춰 줄여요)", seg));

        var r1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        r1.Children.Add(Ui.Text("처음 크기", "label")); r1.Children.Add(_init); r1.Children.Add(_initT);
        var r2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        r2.Children.Add(Ui.Text("최대 크기", "label")); r2.Children.Add(_max); r2.Children.Add(_maxT);
        _custom.Children.Add(r1); _custom.Children.Add(r2);
        p.Children.Add(_custom);
        _init.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { if (_max.Value < _init.Value) _max.Value = _init.Value; UpdatePlan(); } };
        _max.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { if (_init.Value > _max.Value) _init.Value = _max.Value; UpdatePlan(); } };
        _drive.SelectionChanged += (_, _) => UpdatePlan();

        _plan.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
        p.Children.Add(Ui.Row("적용하면", "", _plan));

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        var apply = Ui.Button("적용", "primary");
        apply.Click += async (_, _) =>
        {
            var entries = Planned(out var warn);
            if (entries != null && entries.Any(e => e.MaximumMB < 1024)) { _msg.Text = warn ?? "크기가 너무 작아요"; return; }
            _msg.Text = "관리자 확인 창에서 '예'를 누르세요…";
            _msg.Text = await VirtualMemory.ApplyAsync(entries);
            Refresh();
        };
        _revert.Click += async (_, _) => { _msg.Text = await VirtualMemory.RevertAsync(); Refresh(); };
        _restart.Click += (_, _) =>
        {
            // two clicks: unsaved work in other apps would be lost
            if (_restart.Tag is not "armed")
            {
                _restart.Tag = "armed";
                _restart.Content = "한 번 더 누르면 5초 뒤 다시 시작";
                _msg.Text = "열려 있는 작업을 먼저 저장하세요.";
                return;
            }
            VirtualMemory.Restart();
        };
        bar.Children.Add(apply); bar.Children.Add(_revert); bar.Children.Add(_restart);
        p.Children.Add(bar);
        p.Children.Add(_msg);
        Content = Ui.Card(p);
        Refresh();
    }

    private void Refresh()
    {
        _ram.Text = $"{_ramMB / 1024.0:0} GB";
        var cur = VirtualMemory.Current();
        _current.Text = VirtualMemory.Describe(cur);
        var drives = VirtualMemory.Drives();
        var keep = (_drive.SelectedItem as DriveChoice)?.Drive ?? cur?.FirstOrDefault()?.Drive ?? "C:";
        _drive.ItemsSource = drives;
        _drive.SelectedItem = drives.FirstOrDefault(d => d.Drive == keep) ?? drives.FirstOrDefault();
        if (_init.Value <= 1) { _init.Value = Math.Clamp(_ramMB / 1024.0, 4, 32); _max.Value = _init.Value * 2; }
        _revert.IsEnabled = VirtualMemory.HasBackup;
        UpdatePlan();
    }

    /// null = Windows automatic
    private List<PagefileEntry>? Planned(out string? warning)
    {
        warning = null;
        if (_choice == "auto") return null;
        var d = _drive.SelectedItem as DriveChoice;
        if (d == null) { warning = "드라이브를 고르세요"; return new(); }
        if (_choice == "recommend")
        {
            var (i, m, w) = VirtualMemory.Recommend(_ramMB, d);
            warning = w;
            return new() { new PagefileEntry(d.Drive, i, m) };
        }
        int init = (int)Math.Round(_init.Value) * 1024, max = (int)Math.Round(_max.Value) * 1024;
        if (max > d.FreeMB + VirtualMemory.ActualSizeMB(d.Drive) - 10 * 1024) warning = "이 드라이브의 남은 공간보다 커요. 크기를 줄이세요.";
        return new() { new PagefileEntry(d.Drive, init, max) };
    }

    private void UpdatePlan()
    {
        _initT.Text = $"{Math.Round(_init.Value):0} GB";
        _maxT.Text = $"{Math.Round(_max.Value):0} GB";
        var e = Planned(out var w);
        _plan.Text = (e == null ? "Windows 자동 관리" : e.Count == 0 ? "—" : string.Join(", ", e)) + (w != null ? $"  ·  {w}" : "");
    }
}
