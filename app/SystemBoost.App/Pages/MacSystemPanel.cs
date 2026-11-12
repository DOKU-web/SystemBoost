// macOS: live status (heat, memory, low power) + heavy background apps to quit.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using SystemBoost.App.Services;
using System;

namespace SystemBoost.App.Pages;

public sealed class MacSystemPanel : UserControl
{
    private readonly TextBlock _thermal = Ui.Text("—", "label");
    private readonly TextBlock _memory = Ui.Text("—", "label");
    private readonly TextBlock _lowPower = Ui.Text("—", "label");
    private readonly ListBox _apps = new() { Height = 260, Background = Avalonia.Media.Brushes.Transparent };
    private readonly TextBlock _status = Ui.Text("", "muted", wrap: true);
    private readonly TextBlock _warning = Ui.Text("", "label", wrap: true);   // Time Machine left off, see MacBoostBackend
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MacSystemPanel()
    {
        // left: status (below the toggles of the page) | right: heavy apps
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,*") };

        var st = new StackPanel();
        st.Children.Add(Ui.Text("지금 상태", "h2"));
        _warning.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF7A1A"));
        _warning.IsVisible = false;
        st.Children.Add(_warning);
        st.Children.Add(Ui.Row("발열", "뜨거우면 macOS가 성능을 낮춰요. 통풍·거치대가 도움이 돼요", _thermal, first: true));
        st.Children.Add(Ui.Row("메모리 여유", "부족하면 무거운 앱을 닫으세요", _memory));
        st.Children.Add(Ui.Row("저전력 모드", "", _lowPower));
        var statusCard = Ui.Card(st);
        Grid.SetColumn(statusCard, 0);
        root.Children.Add(statusCard);

        var ap = new StackPanel { Spacing = 10 };
        ap.Children.Add(Ui.Text("무거운 앱 정리", "h2"));
        ap.Children.Add(Ui.Text("CPU·메모리를 많이 쓰는 앱이에요. 고른 앱만 정상 종료를 요청해요(저장 안 한 작업이 있으면 앱이 물어봐요).", "sub", wrap: true));
        ap.Children.Add(_apps);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var quit = Ui.Button("고른 앱 종료", "primary");
        quit.Click += async (_, _) =>
        {
            if (_apps.SelectedItem is not AppUsage a) { _status.Text = "목록에서 앱을 고르세요"; return; }
            await MacBoostBackend.QuitAppAsync(a);
            _status.Text = $"{a.Name}에 종료를 요청했어요";
            await Refresh();
        };
        var refresh = Ui.Button("새로고침", "ghost");
        refresh.Click += async (_, _) => await Refresh();
        bar.Children.Add(quit); bar.Children.Add(refresh);
        ap.Children.Add(bar);
        ap.Children.Add(_status);
        var appsCard = Ui.Card(ap);
        Grid.SetColumn(appsCard, 2);
        root.Children.Add(appsCard);
        Content = root;

        _timer.Tick += async (_, _) => await RefreshStatus();
        AttachedToVisualTree += async (_, _) => { _timer.Start(); await Refresh(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private async System.Threading.Tasks.Task RefreshStatus()
    {
        var w = MacBoostBackend.Current?.Warning;
        _warning.IsVisible = w != null;
        _warning.Text = w ?? "";
        _thermal.Text = await MacBoostBackend.ThermalAsync();
        _memory.Text = await MacBoostBackend.MemoryPressureAsync();
        _lowPower.Text = await MacBoostBackend.LowPowerOnAsync() ? "켜짐 · 성능 낮음" : "꺼짐";
    }

    private async System.Threading.Tasks.Task Refresh()
    {
        await RefreshStatus();
        var sel = (_apps.SelectedItem as AppUsage)?.Pid;
        var list = await MacBoostBackend.TopAppsAsync();
        _apps.ItemsSource = list;
        if (sel != null) _apps.SelectedItem = list.Find(a => a.Pid == sel);
    }
}
