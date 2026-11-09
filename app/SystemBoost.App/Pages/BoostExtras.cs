// Controls under the toggles of the Windows boost pages: the game list and the
// one-click profile, per-game measurement, the standby limit and trim targets,
// GPU tuning values, the CPU's core layout, and for Biz the work programs and startup programs.
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
using System.Threading.Tasks;

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
    private const int CoreOffsetMin = -200, CoreOffsetMax = 100, CoreOffsetStep = 15;
    private const int MemOffsetMin = -500, MemOffsetMax = 300, MemOffsetStep = 50;
    private const int PowerStep = 5;

    public static IEnumerable<Control> For(BoostCategory category, WindowsBoostBackend b, TextBlock status)
    {
        switch (category)
        {
            case BoostCategory.GameBoost:
                yield return Ui.Card(Games(b, status));
                yield return Ui.Card(GameMeasure(b, status));
                yield return Ui.Card(Activity(b));
                break;
            case BoostCategory.Ram:
                yield return Ui.Card(Standby(b, status));
                yield return Ui.Card(TrimTargets(b, status));
                break;
            case BoostCategory.Gpu:
                yield return Ui.Card(GpuTuning(b, status));
                break;
            case BoostCategory.Cpu:
                var cores = WindowsBoostBackend.CoreCounts();
                yield return Ui.Card(Ui.Text(cores is { } c
                    ? $"이 CPU는 하이브리드예요: P코어 쪽 {c.performance}스레드, E코어 {c.efficient}스레드"
                    : "이 CPU는 E코어가 없어서 'E코어로' 항목은 쓸 수 없어요", "muted", wrap: true));
                break;
            case BoostCategory.Biz:
                yield return Ui.Card(WorkApps(b, status));
                yield return Ui.Card(Startup(b, status));
                yield return Ui.Card(Activity(b));
                break;
        }
    }

    private static Control WorkApps(WindowsBoostBackend b, TextBlock status)
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Ui.Text("업무 프로그램", "h2"));
        p.Children.Add(Ui.Text("업무 모드에서 화상 회의 프로그램과 함께 우선순위를 올릴 프로그램을 고르세요. 지금 창이 열려 있는 프로그램이 보여요. "
                               + "우선순위가 '보통'인 프로그램만 올리고, 게임과 시스템 프로그램은 건드리지 않아요.", "muted", wrap: true));
        var list = new StackPanel { Spacing = 2 };
        void Refresh()
        {
            list.Children.Clear();
            var chosen = new HashSet<string>(b.WorkApps, StringComparer.OrdinalIgnoreCase);
            var shown = WindowedApps().Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase)
                                      .Where(n => !WindowsBoostBackend.MeetingApps.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (shown.Count == 0) list.Children.Add(Ui.Text("창이 열린 프로그램이 없어요. 업무 프로그램을 켠 뒤 '목록 새로 고침'을 누르세요.", "muted", wrap: true));
            foreach (var name in shown)
            {
                var c = new CheckBox { Content = name, IsChecked = chosen.Contains(name) };
                c.IsCheckedChanged += (_, _) =>
                {
                    if (c.IsChecked == true) chosen.Add(name); else chosen.Remove(name);
                    b.SetWorkApps(chosen);
                    status.Text = $"업무 프로그램 {chosen.Count}개";
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

    private static Control Startup(WindowsBoostBackend b, TextBlock status)
    {
        var p = new StackPanel { Spacing = 8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text(WindowsBoostBackend.StartupTitle, "h2"));
        head.Children.Add(Ui.Badge(License.PlanOf(PremiumFeature.StartupApps)));
        p.Children.Add(head);
        p.Children.Add(Ui.Text("Windows에 로그인할 때 같이 켜지는 프로그램이에요(이 사용자). 끈 프로그램은 다음 로그인부터 저절로 켜지지 않고, 프로그램과 설정은 지우지 않아요. "
                               + "다시 켜거나 '백업 · 되돌리기'에서 한 번에 원래대로 돌릴 수 있어요. 모든 사용자에게 걸린 항목은 작업 관리자의 '시작 앱'에서 바꿔요.", "muted", wrap: true));
        var list = new StackPanel();
        void Refresh()
        {
            list.Children.Clear();
            List<StartupApps.Item> items;
            try { items = StartupApps.List(StartupApps.User); }
            catch (Exception e) { list.Children.Add(Ui.Text("목록을 읽지 못했어요: " + e.Message, "muted", wrap: true)); return; }
            if (items.Count == 0) list.Children.Add(Ui.Text("이 사용자에게 걸린 시작 프로그램이 없어요.", "muted"));
            bool first = true;
            foreach (var item in items)
            {
                var sw = new ToggleSwitch { OnContent = "", OffContent = "", IsChecked = item.Enabled };
                Avalonia.Automation.AutomationProperties.SetName(sw, item.Name);
                sw.IsCheckedChanged += async (_, _) =>
                {
                    sw.IsEnabled = false;
                    status.Text = await b.SetStartupAsync(item, sw.IsChecked == true);
                    Refresh();   // shows what is set now, also when it was refused
                };
                list.Children.Add(Ui.Row(item.Name, item.Command, sw, first));
                first = false;
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

    // Programs of this user with a window open: what someone works in. Games and system programs are left out.
    private static List<string> WindowedApps()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int session = Process.GetCurrentProcess().SessionId, self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.SessionId != session || p.MainWindowHandle == IntPtr.Zero) continue;
                var name = p.ProcessName;
                if (!ResourceManagers.IsProtected(name) && !ResourceManagers.LooksLikeGame(p.Id, name)) names.Add(name);
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return names.ToList();
    }

    private static Control GameMeasure(WindowsBoostBackend b, TextBlock status)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("게임별 측정", "h2"));
        p.Children.Add(Ui.Text("게임을 실행하고 같은 장면에 가만히 둔 채 '측정'을 누르세요. 약 2분 30초 동안 항목을 껐다 켜며 FPS를 재요. "
                               + "3% 이상 오르고 측정 오차보다 큰 항목만 그 게임에서 켜요.", "muted", wrap: true));
        var results = new StackPanel { Spacing = 6 };
        void Refresh()
        {
            results.Children.Clear();
            if (b.GameProfiles.Count == 0) results.Children.Add(Ui.Text("아직 측정한 게임이 없어요.", "muted"));
            foreach (var (game, profile) in b.GameProfiles)
            {
                results.Children.Add(Ui.Text($"{game} ({profile.MeasuredAt:MM-dd HH:mm})", "label"));
                foreach (var r in profile.Results) results.Children.Add(Ui.Text(Describe(r), "muted", wrap: true));
            }
        }
        p.Children.Add(results);
        var progress = Ui.Text("", "muted", wrap: true);
        var run = Ui.Button("측정", "primary");
        run.HorizontalAlignment = HorizontalAlignment.Left;
        run.Click += async (_, _) =>
        {
            if (!License.Allowed(PremiumFeature.PerGameProfiles))
            {
                status.Text = $"게임별 자동 최적화는 {License.PlanOf(PremiumFeature.PerGameProfiles)} 기능이에요. 왼쪽 '★ 플랜'에서 열 수 있어요.";
                return;
            }
            run.IsEnabled = false;
            var line = await b.MeasureGameAsync(t => Dispatcher.UIThread.Post(() => progress.Text = t));
            progress.Text = "";
            status.Text = line;
            run.IsEnabled = true;
            Refresh();
        };
        p.Children.Add(run);
        p.Children.Add(progress);
        Refresh();
        return p;
    }

    private static string Describe(ProfileResult r)
    {
        var title = BoostCatalog.All.First(f => f.Id == r.Feature).Title;
        if (r.Note.Length > 0) return $"{title}: {r.Note}";
        return $"{title}: 평균 {r.OffFps:0.0} → {r.OnFps:0.0} fps ({r.GainPercent:+0.0;-0.0}%, 오차 {r.NoisePercent:0.0}%), "
             + $"1% 낮은 값 {r.OffLow:0.0} → {r.OnLow:0.0} · " + (r.Keep ? "켜요" : "효과가 확인되지 않아 켜지 않아요");
    }

    private static Control GpuTuning(WindowsBoostBackend b, TextBlock status)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("오버클럭 / 전력 제한 값", "h2"));
        var gpu = WindowsBoostBackend.ReadGpu();
        if (gpu == null)
        {
            p.Children.Add(Ui.Text("NVIDIA 그래픽카드와 최신 드라이버가 있어야 쓸 수 있어요.", "muted", wrap: true));
            return p;
        }
        var now = Ui.Text("", "muted", wrap: true);
        p.Children.Add(now);
        var choice = b.GpuSettings;
        var powers = Steps(gpu.PowerMinW, gpu.PowerMaxW, PowerStep, gpu.PowerDefaultW);
        var power = Picker(powers, choice.PowerW > 0 ? choice.PowerW : gpu.PowerDefaultW, v => v == gpu.PowerDefaultW ? $"{v} W (기본)" : $"{v} W");
        var cores = Steps(CoreOffsetMin, CoreOffsetMax, CoreOffsetStep, 0);
        var core = Picker(cores, choice.CoreMHz, Offset);
        var mems = Steps(MemOffsetMin, MemOffsetMax, MemOffsetStep, 0);
        var mem = Picker(mems, choice.MemMHz, Offset);
        var risk = new CheckBox { Content = "값을 높이면 게임이 멈추거나 화면이 꺼질 수 있다는 것을 이해했어요", IsChecked = choice.RiskAccepted };
        p.Children.Add(Ui.Row("전력 제한", "낮추면 전력과 열이 줄고 성능도 줄 수 있어요", power, true));
        p.Children.Add(Ui.Row("코어 클럭 오프셋", "기본 클럭에 더하거나 빼는 값이에요", core));
        p.Children.Add(Ui.Row("메모리 클럭 오프셋", "기본 클럭에 더하거나 빼는 값이에요", mem));
        p.Children.Add(risk);
        p.Children.Add(Ui.Text($"위 스위치를 켜면 적용돼요. '유지'를 누르지 않거나, GPU가 {choice.TempLimitC} °C 이상이 되거나, 드라이버 오류가 나거나, "
                               + "앱을 끄면 원래 값으로 돌아가요. 제조사 보증 조건은 직접 확인해 주세요.", "muted", wrap: true));
        void Store() => b.SetGpuChoice(powers[power.SelectedIndex], cores[core.SelectedIndex], mems[mem.SelectedIndex], risk.IsChecked == true);
        power.SelectionChanged += (_, _) => Store();
        core.SelectionChanged += (_, _) => Store();
        mem.SelectionChanged += (_, _) => Store();
        risk.IsCheckedChanged += (_, _) => Store();

        var keepText = Ui.Text("", "label", wrap: true);
        var keep = Ui.Button("유지", "primary");
        keep.Click += async (_, _) => status.Text = "오버클럭 / 전력 제한: " + await b.KeepGpuAsync();
        var keepBox = new StackPanel { Spacing = 8, IsVisible = false };
        keepBox.Children.Add(keepText);
        keepBox.Children.Add(keep);
        p.Children.Add(keepBox);

        void Show(GpuReading? g)
        {
            if (g != null) now.Text = $"{g.Name} · 지금 전력 제한 {g.PowerNowW} W, 코어 {Offset(g.CoreMHz)}, 메모리 {Offset(g.MemMHz)}, {g.TempC} °C";
            var left = b.GpuConfirmLeft;
            keepBox.IsVisible = left != null;
            keepText.Text = $"화면과 게임이 정상이면 {left}초 안에 '유지'를 눌러 주세요. 누르지 않으면 원래대로 돌아가요.";
            power.IsEnabled = core.IsEnabled = mem.IsEnabled = !b.GpuTuned;
        }
        Show(gpu);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => Show(await Task.Run(WindowsBoostBackend.ReadGpu));
        p.AttachedToVisualTree += (_, _) => timer.Start();
        p.DetachedFromVisualTree += (_, _) => timer.Stop();
        return p;
    }

    private static string Offset(int mhz) => $"{mhz:+0;-0;0} MHz";

    /// Values from lo to hi that pass through anchor, so the default is always on the list.
    private static List<int> Steps(int lo, int hi, int step, int anchor)
    {
        var below = lo + ((anchor - lo) % step);
        var list = new List<int>();
        for (int v = below; v <= hi; v += step) list.Add(v);
        return list;
    }

    private static ComboBox Picker(List<int> values, int current, Func<int, string> label)
    {
        var box = new ComboBox { ItemsSource = values.Select(label).ToArray(), Width = 170 };
        int at = values.IndexOf(current);
        box.SelectedIndex = at >= 0 ? at : values.IndexOf(values.OrderBy(v => Math.Abs(v - current)).First());
        return box;
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
                if (p.SessionId == session && p.WorkingSet64 >= ListedAppMinBytes && !ResourceManagers.LooksLikeGame(p.Id, p.ProcessName)) names.Add(p.ProcessName);
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

    // Windows 10 2004 and later: screen capture and broadcasts leave the window out; it stays on the screen.
    private const uint ExcludeFromCapture = 0x11;
    [System.Runtime.InteropServices.DllImport("user32")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// hideFromCapture: while streaming, so the notice is not in the broadcast.
    public NoticeWindow(string text, bool hideFromCapture = false)
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
            if (hideFromCapture && OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is IntPtr h && h != IntPtr.Zero)
                SetWindowDisplayAffinity(h, ExcludeFromCapture);
            if (Screens.Primary is { } s) Position = new PixelPoint(s.WorkingArea.Right - (int)(Bounds.Width * s.Scaling) - 16, s.WorkingArea.Y + 16);
            DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(ShownMs));
        };
    }
}
