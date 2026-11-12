using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using SystemBoost.App.Services;
using SystemBoost.App.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SystemBoost.App.Pages;

// ---------------------------------------------------------------- dashboard
public sealed class DashboardPage : UserControl
{
    public DashboardPage(FrameGenViewModel fg, IBoostBackend backend, Action<string> go)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.Header("대시보드", $"{Platform.Name} · SystemBoost {Updater.CurrentVersion.Split('+')[0]}"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,*,20,*") };

        Border Tile(string title, string body, string button, string target, int col)
        {
            var p = new StackPanel { Spacing = 10 };
            p.Children.Add(Ui.Text(title, "h2"));
            p.Children.Add(Ui.Text(body, "sub", wrap: true));
            var b = Ui.Button(button, "primary");
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Click += (_, _) => go(target);
            p.Children.Add(b);
            var c = Ui.Card(p);
            Grid.SetColumn(c, col);
            return c;
        }
        grid.Children.Add(Tile("프레임 생성", "게임이 지원하지 않아도 중간 프레임을 만들고, 낮은 해상도 창을 화면 크기로 키워요.", "열기", "fg", 0));
        if (Platform.IsWindows)
        {
            grid.Children.Add(Tile("게임 부스트", backend.Connected
                ? "선택한 프로필의 최적화를 한 번에 적용하고, 게임이 끝나면 되돌려요."
                : "준비 중이에요. 열리면 여기서 바로 적용할 수 있어요.", "열기", "boost", 2));
            grid.Children.Add(Tile("벤치마크", "적용 전/후를 같은 장면에서 측정해서 효과가 있는 것만 남기세요.", "기록 보기", "bench", 4));
        }
        else
        {
            grid.Children.Add(Tile("게임 부스트", "저전력 모드 끄기, 백업·색인 멈추기, 무거운 앱 정리, 발열·메모리 상태를 한 화면에서 봐요.", "열기", "boost", 2));
            var mac = new StackPanel { Spacing = 8 };
            mac.Children.Add(Ui.Text("윈도우 게임 (macOS)", "h2"));
            mac.Children.Add(Ui.Text("윈도우용 게임을 Wine으로 실행하고 프레임 생성을 걸 수 있어요. 안티치트 온라인 게임은 대부분 안 돼요.", "sub", wrap: true));
            var wb = Ui.Button("윈도우 게임 열기", "primary");
            wb.HorizontalAlignment = HorizontalAlignment.Left;
            wb.Click += (_, _) => go("wine");
            mac.Children.Add(wb);
            var c = Ui.Card(mac);
            Grid.SetColumn(c, 4);
            grid.Children.Add(c);
        }
        root.Children.Add(grid);
        if (OperatingSystem.IsWindows()) root.Children.Add(Ui.Card(FpsOverlay.Card()));
        Content = new ScrollViewer { Content = root };
    }
}

// ------------------------------------------------------------- boost pages
public sealed class BoostPage : UserControl
{
    public BoostPage(BoostCategory category, string title, IBoostBackend backend, BoostCategory? extra = null)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.Header(title, backend.Connected
            ? "모든 변경은 적용 전에 자동으로 백업되고, '백업 · 되돌리기'에서 한 번에 원래대로 돌릴 수 있어요."
            : "'준비 중' 항목은 아직 켤 수 없고, 시스템을 바꾸지 않아요."));
        var status = Ui.Text("", "muted", wrap: true);
        status.Margin = new Thickness(0, 0, 0, 12);
        root.Children.Add(status);

        var list = new StackPanel();
        bool first = true;
        var toggles = new List<(ToggleSwitch sw, string id)>();
        var features = BoostCatalog.For(category).ToList();
        if (extra is BoostCategory e) features.AddRange(BoostCatalog.For(e));
        foreach (var f in features)
        {
            var sw = new ToggleSwitch { OnContent = "", OffContent = "", Tag = "loading" };
            Avalonia.Automation.AutomationProperties.SetName(sw, f.Title);
            sw.IsCheckedChanged += async (_, _) =>
            {
                if (sw.Tag is "loading") return;  // initial state from the backend, not a user change
                if (f.Needs is { } need && sw.IsChecked == true && !License.Allowed(need))
                {
                    sw.Tag = "loading"; sw.IsChecked = false; sw.Tag = null;
                    status.Text = $"{f.Title}은(는) {License.PlanOf(need)} 기능이에요. 왼쪽 '★ 플랜'에서 열 수 있어요.";
                    return;
                }
                sw.IsEnabled = false;
                status.Text = $"{f.Title}: 처리하는 중…";
                string r = sw.IsChecked == true ? await backend.ApplyAsync(f.Id) : await backend.RevertAsync(f.Id);
                status.Text = $"{f.Title}: {r}";
                await Init(sw, backend, f.Id);   // a declined prompt or a failure leaves it off
                sw.IsEnabled = true;
            };
            var desc = f.Description;
            if (f.RequiresAdmin) desc = (desc.Length > 0 ? desc + " · " : "") + "관리자 권한 필요";
            if (f.NeedsReboot) desc += " · 재부팅 후 적용";
            bool ready = backend.Connected;
            var row = Ui.Row(f.Title, desc, ready ? sw : Ui.Pending(), first);
            if (f.Needs is { } plan && row.Child is Grid g && g.Children[0] is StackPanel sp && sp.Children[0] is TextBlock tb)
            {
                sp.Children.RemoveAt(0);
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(tb);
                line.Children.Add(Ui.Badge(License.PlanOf(plan)));
                sp.Children.Insert(0, line);
            }
            if (!ready && row.Child is Grid pending) pending.Children[0].Opacity = 0.55;
            list.Children.Add(row);
            first = false;
            if (ready)
            {
                _ = Init(sw, backend, f.Id);
                toggles.Add((sw, f.Id));
            }
        }
        if (Platform.IsMac && category == BoostCategory.GameBoost)
        {
            // toggles on the left half, status + heavy apps from MacSystemPanel below/right
            var card = Ui.Card(list);
            var half = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,*") };
            half.Children.Add(card);
            root.Children.Add(half);
            root.Children.Add(new MacSystemPanel { Margin = new Thickness(0, 20, 0, 0) });
        }
        else root.Children.Add(Ui.Card(list));
        if (OperatingSystem.IsWindows() && backend is WindowsBoostBackend win)
        {
            foreach (var c in BoostExtras.For(category, win, status)) root.Children.Add(c);
            win.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                foreach (var (sw, id) in toggles) if (sw.IsEnabled) _ = Init(sw, backend, id);
            });
        }
        if (Platform.IsWindows && category == BoostCategory.Ram) root.Children.Add(new VirtualMemoryCard());
        Content = new ScrollViewer { Content = root };
    }

    private static async System.Threading.Tasks.Task Init(ToggleSwitch sw, IBoostBackend b, string id)
    {
        var applied = await b.IsAppliedAsync(id);
        sw.Tag = "loading";
        sw.IsChecked = applied;
        sw.Tag = null;
    }
}

// ------------------------------------------------------------- benchmark
public sealed class BenchmarkPage : UserControl
{
    public BenchmarkPage()
    {
        var root = new DockPanel();
        var h = Ui.Header("벤치마크 · 기록", "프레임 생성 세션이 끝날 때마다 평균 원본/출력 FPS, 추가 지연, 늦은 프레임 수를 기록해요. 기록은 이 PC에만 저장돼요.");
        DockPanel.SetDock(h, Dock.Top);
        root.Children.Add(h);
        var list = new ListBox { ItemsSource = FrameGenViewModel.Sessions, Background = Avalonia.Media.Brushes.Transparent };
        var tip = Ui.Text("같은 게임, 같은 장면에서 끄기 / 2배 / 자동을 차례로 돌려 비교하세요. 게임 FPS 제한을 걸면 비교가 정확해져요.", "muted", wrap: true);
        tip.Margin = new Thickness(0, 0, 0, 12);
        var p = new DockPanel();
        DockPanel.SetDock(tip, Dock.Top);
        p.Children.Add(tip);
        p.Children.Add(list);
        root.Children.Add(Ui.Card(p));
        Content = root;
    }
}

// ------------------------------------------------------------- backup
public sealed class BackupPage : UserControl
{
    public BackupPage(IBoostBackend backend)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.Header("백업 · 되돌리기", Platform.IsMac
            ? "SystemBoost가 바꾼 설정(저전력 모드, Time Machine, Spotlight)을 한 번에 원래대로 돌려요. 앱을 지우기 전에 꼭 눌러 주세요. 지운 뒤에는 SystemBoost가 되돌릴 수 없어요."
            : backend.Connected
                ? "SystemBoost가 바꾼 설정(레지스트리, 전원 계획, 드라이버 프로필)을 한 번에 원래대로 돌려요. 앱을 지우기 전에도 눌러 주세요. 가상 메모리는 'RAM 확보' 화면의 '원래대로'로 되돌려요."
                : "부스트 항목이 준비 중이라 아직 되돌릴 설정이 없어요. 가상 메모리는 'RAM 확보' 화면의 '원래대로'로 되돌려요."));
        var result = Ui.Text("", "muted", wrap: true);
        var btn = Ui.Button(backend.Connected ? "전체 되돌리기" : "준비 중", "primary");
        btn.IsEnabled = backend.Connected;
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        btn.Click += async (_, _) => result.Text = await backend.RevertAllAsync();
        var p = new StackPanel { Spacing = 12 };
        if (OperatingSystem.IsWindows() && backend is WindowsBoostBackend win)
        {
            var changed = Ui.Text("", "label", wrap: true);
            // the uninstaller runs without administrator rights: it asks once, and what is refused there stays
            var admin = Ui.Text("", "label", wrap: true);
            admin.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF7A1A"));
            void Refresh()
            {
                var items = win.ChangedItems();
                changed.Text = items.Count == 0 ? "지금 바뀐 항목이 없어요." : "지금 바뀐 항목: " + string.Join(", ", items);
                var left = win.AdminItemsLeft();
                admin.IsVisible = left.Count > 0;
                admin.Text = $"삭제 전에 '전체 되돌리기'를 눌러 주세요. 관리자 권한으로 바꾼 설정은 앱을 지울 때 한 번 묻고 되돌리는데, 그때 '아니요'를 누르면 그대로 남아요: {string.Join(", ", left)}";
            }
            Refresh();
            win.Changed += () => Dispatcher.UIThread.Post(Refresh);
            p.Children.Add(changed);
            p.Children.Add(admin);
        }
        p.Children.Add(btn);
        p.Children.Add(result);
        root.Children.Add(Ui.Card(p));
        Content = root;
    }
}

// ------------------------------------------------------------- settings
public sealed class SettingsPage : UserControl
{
    public SettingsPage(IBoostBackend backend)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.Header("설정", "버전과 구성 요소 상태"));
        var list = new StackPanel();
        list.Children.Add(Ui.Row("버전", "", Ui.Text(Updater.CurrentVersion, "label"), first: true));
        list.Children.Add(Ui.Row("운영체제", "", Ui.Text(Platform.Name, "label")));
        list.Children.Add(Ui.Row("프레임 생성 엔진", Platform.EnginePath, Ui.Text(Platform.EngineAvailable ? "설치됨" : "없음", "label")));
        if (Platform.IsWindows)
            list.Children.Add(Ui.Row("부스트 기능", "RAM·GPU·CPU·시스템 최적화", Ui.Text(backend.Connected ? "연결됨" : "준비 중", "label")));
        bool portable = Platform.Portable;
        list.Children.Add(Ui.Row("설치 형태", portable ? "설정과 기록을 이 폴더에 저장해요" : "설정과 기록을 사용자 폴더에 저장해요",
                                 Ui.Text(portable ? "포터블" : "설치판", "label")));
        root.Children.Add(Ui.Card(list));
        root.Children.Add(Ui.Card(UpdateCard()));
        root.Children.Add(Ui.Card(ShortcutCard()));
        root.Children.Add(Ui.Card(UsageCard()));
        root.Children.Add(Ui.Card(LegalCard()));
        Content = new ScrollViewer { Content = root };
    }

    private static Control ShortcutCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("바탕화면 바로 가기", "h2"));
        var status = Ui.Text("", "muted", wrap: true);
        var add = Ui.Button("바탕화면에 바로 가기 추가", "primary");
        var remove = Ui.Button("바로 가기 지우기", "ghost");
        add.HorizontalAlignment = remove.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        void Refresh(string? error = null)
        {
            var exists = DesktopShortcut.Exists;
            add.IsVisible = !exists;
            remove.IsVisible = exists;
            status.Text = error ?? (exists ? "바탕화면에 SystemBoost 바로 가기가 있어요." : "바탕화면에 바로 가기가 없어요.");
        }
        add.Click += (_, _) => Refresh(DesktopShortcut.Create());
        remove.Click += (_, _) => Refresh(DesktopShortcut.Remove());
        Refresh();
        p.Children.Add(status);
        p.Children.Add(add);
        p.Children.Add(remove);
        if (Platform.Portable) p.Children.Add(Ui.Text("포터블은 폴더를 옮기면 바로 가기를 지우고 다시 만들어야 해요.", "muted", wrap: true));
        return p;
    }

    private static Control UsageCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("사용 통계", "h2"));
        var on = new CheckBox { Content = "사용 통계 보내기", IsChecked = Telemetry.Enabled };
        on.IsCheckedChanged += (_, _) => Telemetry.Enabled = on.IsChecked == true;
        p.Children.Add(on);
        p.Children.Add(Ui.Text("앱이 켜져 있는 동안 무작위 설치 번호, 앱 버전, 운영체제, 플랜, 프레임 생성 배수, 원클릭 게임 부스트 사용 여부를 판매자 서버로 보내요. " +
                               "서버는 접속한 국가를 함께 기록하고 IP 주소는 저장하지 않아요. 키, 메일, 게임 이름, 하드웨어 정보는 보내지 않아요. 끄면 이 PC의 설치 번호도 지워요.", "muted", wrap: true));
        return p;
    }

    // Who made it and how (LICENSE.txt says the same; no copyright is claimed); the full texts ship next to the app.
    private static Control LegalCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("저작권과 오픈소스 고지", "h2"));
        p.Children.Add(Ui.Text("만든 사람: DOKKU · 코드와 로고는 AI 도구로 만들었어요", "label", wrap: true));
        p.Children.Add(Ui.Text("SystemBoost에는 오픈소스 구성 요소가 들어 있어요. 각 구성 요소의 저작권은 해당 권리자에게 있고, 각자의 라이선스를 따라요.", "muted", wrap: true));
        p.Children.Add(Ui.Text(
            "Avalonia (MIT) · ANGLE (BSD-3-Clause) · SkiaSharp, HarfBuzzSharp (MIT) · MicroCom.Runtime (MIT) · " +
            "NuGet.Versioning (Apache-2.0) · Tmds.DBus.Protocol (MIT) · Velopack (MIT) · .NET 8 (MIT) · NVAPI (MIT) · " +
            "C++/WinRT (MIT) · Microsoft C/C++ runtime · " +
            "Velopack Rust crates (MIT, Apache-2.0, ISC, BSD-3-Clause, Zlib, Unicode-3.0, CDLA-Permissive-2.0)", "muted", wrap: true));
        if (Platform.IsMac)
            p.Children.Add(Ui.Text("윈도우 게임 기능은 Wine (LGPL-2.1-or-later), DXVK-macOS (zlib), MoltenVK (Apache-2.0)를 처음 쓸 때 내려받아요. Wine 소스: https://gitlab.winehq.org/wine/wine", "muted", wrap: true));
        var status = Ui.Text("전문 파일을 찾을 수 없어요. 앱을 다시 설치해 주세요.", "muted", wrap: true);
        status.IsVisible = false;
        var open = Ui.Button("라이선스 전문 보기", "ghost");
        open.Click += async (_, _) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
            if (!File.Exists(path)) { status.IsVisible = true; return; }
            if (TopLevel.GetTopLevel(open) is Window owner) await new NoticesDialog(path).ShowDialog(owner);
        };
        var eula = Ui.Button("사용권 계약 보기", "ghost");
        eula.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(eula) is Window owner) await new EulaDialog(consent: false).ShowDialog(owner);
        };
        var privacyStatus = Ui.Text("", "muted", wrap: true);
        privacyStatus.IsVisible = false;
        var buttons = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var b in new[] { eula, Ui.PrivacyButton(privacyStatus), open })
        {
            b.Margin = new Thickness(0, 0, 10, 6);
            buttons.Children.Add(b);
        }
        p.Children.Add(buttons);
        p.Children.Add(status);
        p.Children.Add(privacyStatus);
        return p;
    }

    // automatic updates (installer / portable / macOS builds; Steam updates itself)
    private static Control UpdateCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("업데이트", "h2"));
        if (!Updater.Supported)
        {
            p.Children.Add(Ui.Text("이 버전은 자동 업데이트를 쓰지 않아요. 스팀에서 받았다면 스팀이 업데이트해 줘요.", "muted", wrap: true));
            return p;
        }
        var auto = new CheckBox { Content = "새 버전을 자동으로 확인하고 미리 받아 두기", IsChecked = Updater.AutoCheck };
        auto.IsCheckedChanged += (_, _) => Updater.AutoCheck = auto.IsChecked == true;
        p.Children.Add(auto);
        var status = Ui.Text("", "label", wrap: true);
        var notes = Ui.Text("", "muted", wrap: true);
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        var check = Ui.Button("지금 확인", "ghost");
        check.Click += async (_, _) => await Updater.CheckAndDownloadAsync(userAsked: true);
        var restart = Ui.Button("다시 시작해서 업데이트", "primary");
        restart.Click += (_, _) =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                && d.MainWindow is MainWindow w) w.Shutdown(applyUpdateOnExit: false);
            Updater.ApplyAndRestart();
        };
        row.Children.Add(check);
        row.Children.Add(restart);
        p.Children.Add(status);
        p.Children.Add(row);
        p.Children.Add(notes);
        void Refresh()
        {
            status.Text = Updater.State switch
            {
                UpdateState.Checking => "새 버전을 확인하는 중…",
                UpdateState.UpToDate => $"최신 버전이에요 ({Updater.CurrentVersion}) · {Updater.LastCheck:HH:mm} 확인",
                UpdateState.Downloading => $"새 버전 {Updater.NewVersion} 받는 중… {Updater.Progress}%",
                UpdateState.Ready => $"새 버전 {Updater.NewVersion}이 준비됐어요. 다시 시작하거나 앱을 끌 때 적용돼요.",
                UpdateState.Failed => Updater.Error ?? "업데이트를 확인하지 못했어요",
                _ => $"현재 버전 {Updater.CurrentVersion}",
            };
            check.IsEnabled = Updater.State is not (UpdateState.Checking or UpdateState.Downloading);
            restart.IsVisible = Updater.State == UpdateState.Ready;
            notes.Text = string.IsNullOrWhiteSpace(Updater.ReleaseNotes) ? "" : "바뀐 점\n" + Updater.ReleaseNotes.Trim();
            notes.IsVisible = notes.Text.Length > 0;
        }
        Updater.Changed += Refresh;
        Refresh();
        return p;
    }
}
