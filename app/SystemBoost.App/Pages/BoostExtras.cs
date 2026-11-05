// Controls under the toggles of the Windows boost pages: the game list and the
// one-click profile, the standby limit and trim targets, the CPU's core layout.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SystemBoost.App.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace SystemBoost.App.Pages;

[SupportedOSPlatform("windows")]
internal static class BoostExtras
{
    private const long ListedAppMinBytes = 100 * 1048576;
    private static readonly (string id, string label)[] Profiles =
    {
        ("basic", "기본: 관리자 권한 없이 쓰는 항목"),
        ("full", "전체: 대기 메모리 정리까지 (관리자 권한 필요)"),
    };
    private static readonly int[] Limits = { 512, 1024, 2048, 4096 };

    public static IEnumerable<Control> For(BoostCategory category, WindowsBoostBackend b, TextBlock status)
    {
        switch (category)
        {
            case BoostCategory.GameBoost:
                yield return Ui.Card(Games(b, status));
                yield return Ui.Card(Activity(b));
                break;
            case BoostCategory.Ram:
                yield return Ui.Card(Standby(b, status));
                yield return Ui.Card(TrimTargets(b, status));
                break;
            case BoostCategory.Cpu:
                var cores = WindowsBoostBackend.CoreCounts();
                yield return Ui.Card(Ui.Text(cores is { } c
                    ? $"이 CPU는 하이브리드예요: P코어 쪽 {c.performance}스레드, E코어 {c.efficient}스레드"
                    : "이 CPU는 E코어가 없어서 'E코어로' 항목은 쓸 수 없어요", "muted", wrap: true));
                break;
        }
    }

    private static Control Games(WindowsBoostBackend b, TextBlock status)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("내 게임", "h2"));
        p.Children.Add(Ui.Text("이 목록의 게임이 켜지면 '게임 중' 항목이 동작하고, 꺼지면 원래대로 돌아가요. 게임은 프로세스 이름으로만 알아보고, 게임 파일과 메모리는 건드리지 않아요.", "muted", wrap: true));
        var list = new StackPanel { Spacing = 6 };
        void Refresh()
        {
            list.Children.Clear();
            if (b.Games.Count == 0) list.Children.Add(Ui.Text("아직 없어요. 게임의 실행 파일(.exe)을 추가하세요.", "muted"));
            foreach (var game in b.Games)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var left = new StackPanel();
                left.Children.Add(Ui.Text(Path.GetFileNameWithoutExtension(game), "label"));
                left.Children.Add(Ui.Text(game, "muted"));
                row.Children.Add(left);
                var remove = Ui.Button("빼기", "ghost");
                remove.Click += async (_, _) => status.Text = await b.RemoveGameAsync(game);
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                list.Children.Add(row);
            }
        }
        p.Children.Add(list);
        var add = Ui.Button("게임 추가", "primary");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(p);
            if (top == null) return;
            var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "게임 실행 파일 고르기", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("실행 파일") { Patterns = new[] { "*.exe" } } },
            });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is string path) status.Text = await b.AddGameAsync(path);
        };
        p.Children.Add(add);

        var profile = new ComboBox { ItemsSource = Profiles.Select(x => x.label).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        profile.SelectedIndex = Math.Max(0, Array.FindIndex(Profiles, x => x.id == b.Profile));
        profile.SelectionChanged += (_, _) => { if (profile.SelectedIndex >= 0) b.SetProfile(Profiles[profile.SelectedIndex].id); };
        p.Children.Add(Ui.Row("원클릭 프로필", "원클릭 게임 부스트를 켤 때 함께 켤 항목", profile));
        profile.Width = 340;

        Refresh();
        b.Changed += () => Dispatcher.UIThread.Post(Refresh);
        return p;
    }

    private static Control Activity(WindowsBoostBackend b)
    {
        var p = new StackPanel { Spacing = 6 };
        p.Children.Add(Ui.Text("최근 동작", "h2"));
        var lines = Ui.Text("", "muted", wrap: true);
        void Refresh() => lines.Text = b.Activity.Count == 0 ? "아직 기록이 없어요." : string.Join("\n", b.Activity.Take(10));
        p.Children.Add(lines);
        Refresh();
        b.Changed += () => Dispatcher.UIThread.Post(Refresh);
        return p;
    }

    private static Control Standby(WindowsBoostBackend b, TextBlock status)
    {
        var p = new StackPanel { Spacing = 10 };
        var limit = new ComboBox { ItemsSource = Limits.Select(mb => $"{mb} MB").ToArray(), Width = 140 };
        limit.SelectedIndex = Math.Max(0, Array.IndexOf(Limits, b.StandbyLimitMB));
        limit.SelectionChanged += (_, _) => { if (limit.SelectedIndex >= 0) b.SetStandbyLimit(Limits[limit.SelectedIndex]); };
        p.Children.Add(Ui.Row("대기 메모리 정리 기준", "게임 중 여유 메모리가 이 값 아래로 떨어지면 정리해요", limit, first: true));
        var now = Ui.Button("지금 정리", "ghost");
        now.HorizontalAlignment = HorizontalAlignment.Left;
        now.Click += async (_, _) =>
        {
            now.IsEnabled = false;
            status.Text = await b.PurgeStandbyAsync();
            now.IsEnabled = true;
        };
        p.Children.Add(now);
        return p;
    }

    private static Control TrimTargets(WindowsBoostBackend b, TextBlock status)
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Ui.Text("작업 집합 정리 대상", "h2"));
        p.Children.Add(Ui.Text("게임 중 메모리를 비울 프로그램을 고르세요. 지금 메모리를 많이 쓰는 프로그램이 보여요.", "muted", wrap: true));
        var list = new StackPanel { Spacing = 2 };
        void Refresh()
        {
            list.Children.Clear();
            var chosen = new HashSet<string>(b.TrimTargets, StringComparer.OrdinalIgnoreCase);
            foreach (var name in RunningApps().Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var c = new CheckBox { Content = name, IsChecked = chosen.Contains(name) };
                c.IsCheckedChanged += (_, _) =>
                {
                    if (c.IsChecked == true) chosen.Add(name); else chosen.Remove(name);
                    b.SetTrimTargets(chosen);
                    status.Text = $"대상 프로그램 {chosen.Count}개";
                };
                list.Children.Add(c);
            }
        }
        var again = Ui.Button("목록 새로 고침", "ghost");
        again.HorizontalAlignment = HorizontalAlignment.Left;
        again.Click += (_, _) => Refresh();
        p.Children.Add(list);
        p.Children.Add(again);
        Refresh();
        return p;
    }

    private static List<string> RunningApps()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId == session && p.WorkingSet64 >= ListedAppMinBytes && !ResourceManagers.IsProtected(p.ProcessName)) names.Add(p.ProcessName);
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return names.ToList();
    }
}

/// Small notice in the corner that does not take the focus from the game.
internal sealed class NoticeWindow : Window
{
    private const int ShownMs = 7000;

    public NoticeWindow(string text)
    {
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#16161A"));
        var t = Ui.Text(text, "label");
        Content = new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF7A1A")), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10), Child = t,
        };
        Opened += (_, _) =>
        {
            if (Screens.Primary is { } s) Position = new PixelPoint(s.WorkingArea.Right - (int)(Bounds.Width * s.Scaling) - 16, s.WorkingArea.Y + 16);
            DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(ShownMs));
        };
    }
}
