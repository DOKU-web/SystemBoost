using Avalonia.Controls;
using SystemBoost.App.Pages;
using SystemBoost.App.Services;
using SystemBoost.App.ViewModels;
using System;
using System.Collections.Generic;

namespace SystemBoost.App;

public partial class MainWindow : Window
{
    private readonly FrameGenViewModel _frameGen = new();
    private readonly IBoostBackend _backend = Platform.IsMac ? new MacBoostBackend() : CreateWindowsBackend();
    private readonly IResourceManager _resources = ResourceManagers.Create();
    private readonly Dictionary<Button, Func<Control>> _pages = new();

    private static IBoostBackend CreateWindowsBackend()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var b = new WindowsBoostBackend();
        b.Notice += text => Avalonia.Threading.Dispatcher.UIThread.Post(() => new NoticeWindow(text).Show());
        return b;
    }
    private readonly Dictionary<string, Control> _cache = new();

    public MainWindow()
    {
        InitializeComponent();
        AddNav("◎   대시보드", "dash", () => new DashboardPage(_frameGen, _backend, Go));
        if (BoostCatalog.CategoryAvailable(BoostCategory.GameBoost))
            AddNav("🚀   게임 부스트", "boost", () => new BoostPage(BoostCategory.GameBoost, "게임 부스트", _backend));
        AddNav("▣   프레임 생성", "fg", () => new FrameGenPage(_frameGen));
        AddNav("◔   자원 분배", "share", () => new ResourcePage(_resources));
        if (BoostCatalog.CategoryAvailable(BoostCategory.Ram))
            AddNav("▤   RAM 확보", "ram", () => new BoostPage(BoostCategory.Ram, "RAM 확보", _backend));
        if (BoostCatalog.CategoryAvailable(BoostCategory.Gpu))
            AddNav("▥   GPU · 오버클럭", "gpu", () => new BoostPage(BoostCategory.Gpu, "GPU · 오버클럭", _backend));
        if (BoostCatalog.CategoryAvailable(BoostCategory.Cpu))
            AddNav("▦   CPU", "cpu", () => new BoostPage(BoostCategory.Cpu, "CPU", _backend));
        if (BoostCatalog.CategoryAvailable(BoostCategory.Network))
            AddNav("◇   네트워크 · 시스템", "net", () => new BoostPage(BoostCategory.Network, "네트워크 · 시스템", _backend, BoostCategory.System));
        if (Platform.IsMac)
            AddNav("🍷   윈도우 게임", "wine", () => new WindowsGamesPage(Go));
        AddNav("▧   벤치마크 · 기록", "bench", () => new BenchmarkPage());
        AddNav("★   프리미엄", "premium", () => new PremiumPage(_backend.Connected));
        AddNav("◈   계정", "account", () => new AccountPage(OpenPlans));
        AddNav("↺   백업 · 되돌리기", "backup", () => new BackupPage(_backend));
        NavSettings.Click += (_, _) => Show(NavSettings, "settings", () => new SettingsPage(_backend));
        UpdatePlan();
        License.Changed += UpdatePlan;
        PlanCard.PointerPressed += (_, _) => OpenPlans();
        Updater.Changed += UpdateBar;
        UpdateNotes.Click += (_, _) => Show(NavSettings, "settings", () => new SettingsPage(_backend));
        UpdateLater.Click += (_, _) => { _updateDismissed = true; UpdateBar(); };
        UpdateRestart.Click += (_, _) => { Shutdown(applyUpdateOnExit: false); Updater.ApplyAndRestart(); };
        UpdateBar();
        License.KeyArrived += async (key, err) =>   // seller confirmed a payment -> key popup
        {
            Go("premium");
            await new KeyDialog(key, err).ShowDialog(this);
        };
        Go("fg");
        if (OperatingSystem.IsWindows() && _backend is WindowsBoostBackend win) _ = win.StartAsync();
    }

    private bool _plansOpen;

    private async void OpenPlans()
    {
        if (_plansOpen) return;
        _plansOpen = true;
        var plans = new PlanDialog(_backend.Connected);
        await plans.ShowDialog(this);
        _plansOpen = false;
        if (plans.Choice is not { } pick) return;
        Go("premium");
        (PageHost.Content as PremiumPage)?.Choose(pick.Ultra, pick.Period);
    }

    private readonly Dictionary<string, Button> _navByKey = new();

    private void AddNav(string text, string key, Func<Control> make)
    {
        var b = new Button { Content = text };
        b.Classes.Add("nav");
        b.Click += (_, _) => Show(b, key, make);
        NavPanel.Children.Add(b);
        _pages[b] = make;
        _navByKey[key] = b;
    }

    private void Go(string key)
    {
        if (_navByKey.TryGetValue(key, out var b)) Show(b, key, _pages[b]);
    }

    private void Show(Button b, string key, Func<Control> make)
    {
        foreach (var child in NavPanel.Children)
            if (child is Button nb) nb.Classes.Set("on", nb == b);
        NavSettings.Classes.Set("on", b == NavSettings);
        if (!_cache.TryGetValue(key, out var page)) { page = make(); _cache[key] = page; }
        PageHost.Content = page;
    }

    private void UpdatePlan()
    {
        PlanTitle.Text = License.IsUltra ? "울트라 ★" : License.IsPremium ? "프리미엄 ★" : "무료";
        PlanText.Text = License.IsUltra ? "모든 기능 사용 중" : License.IsPremium ? "프리미엄 기능 사용 중" : "누르면 플랜 업그레이드";
    }

    // What the usage statistics report about this moment.
    public async System.Threading.Tasks.Task<(int frameGen, bool boost)> UsageNow() =>
        (_frameGen.UsageMultiplier, await _backend.IsAppliedAsync("boost.oneclick"));

    public void Shutdown(bool applyUpdateOnExit = true)
    {
        if (_shutDown) return;
        _shutDown = true;
        _frameGen.Dispose();
        _resources.Dispose();   // puts every app's priority / limits back
        (_backend as IDisposable)?.Dispose();   // what was only for the running game goes back
        if (applyUpdateOnExit) Updater.ApplyOnExit();  // a downloaded update is installed after the app closes
    }
    private bool _shutDown;

    // ------------------------------------------------------------ update bar
    private void UpdateBar()
    {
        var show = Updater.State == UpdateState.Ready && !_updateDismissed;
        UpdateBarBox.IsVisible = show;
        if (!show) return;
        UpdateBarText.Text = $"새 버전({Updater.NewVersion})이 준비됐어요. 지금 다시 시작하거나, 앱을 끌 때 자동으로 적용돼요.";
        UpdateRestart.Content = _frameGen.Running ? "프레임 생성 끄고 다시 시작" : "지금 다시 시작";
    }
    private bool _updateDismissed;

    protected override void OnClosed(EventArgs e)
    {
        Shutdown();
        base.OnClosed(e);
    }
}
