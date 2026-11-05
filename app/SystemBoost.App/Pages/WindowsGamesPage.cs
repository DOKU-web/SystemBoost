// macOS only: install the Wine runtime, manage games, launch them, then attach
// frame generation to the game window.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SystemBoost.App.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SystemBoost.App.Pages;

public sealed class WindowsGamesPage : UserControl
{
    private readonly WineManager _wine = new();
    private readonly Action<string> _go;
    private readonly TextBlock _runtimeStatus = Ui.Text("", "sub", wrap: true);
    private readonly TextBlock _progressText = Ui.Text("", "muted", wrap: true);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly TextBlock _gptkText = Ui.Text("", "muted", wrap: true);
    private readonly ListBox _games = new() { Height = 220, Background = Avalonia.Media.Brushes.Transparent };
    private readonly ComboBox _backend = new() { ItemsSource = new[] { "DXVK (DirectX 10·11, 기본)", "WineD3D (DirectX 9 이하·호환성)", "D3DMetal (DirectX 11·12, GPTK 필요)" } };
    private readonly TextBlock _status = Ui.Text("", "muted", wrap: true);
    private CancellationTokenSource? _cts;

    public WindowsGamesPage(Action<string> go)
    {
        _go = go;
        var root = new StackPanel { Spacing = 20 };
        root.Children.Add(Ui.Header("윈도우 게임 실행 (macOS)",
            "Wine 호환 계층으로 윈도우용 게임을 실행하고, 실행한 게임 창에 프레임 생성·업스케일을 걸 수 있어요."));

        // limits, shown before anything is installed
        var warn = new StackPanel { Spacing = 4 };
        warn.Children.Add(Ui.Text("알아두세요", "h2"));
        foreach (var line in new[]
        {
            "· 안티치트(EasyAntiCheat, BattlEye, Vanguard 등)를 쓰는 온라인 게임은 대부분 실행되지 않아요.",
            "· 게임마다 되는 정도가 달라요. 실행돼도 윈도우 PC보다 느릴 수 있어요.",
            "· DirectX 12 게임은 Apple Game Porting Toolkit(무료, Apple 개발자 계정 필요)을 직접 받아 지정해야 해요.",
            "· 처음 설치 때 약 200MB를 받아요. Wine·DXVK는 오픈소스를 수정 없이 받아 쓰고, 파일 해시를 확인해요.",
        }) warn.Children.Add(Ui.Text(line, "sub", wrap: true));
        root.Children.Add(Ui.Card(warn, "note"));

        // runtime
        var rt = new StackPanel { Spacing = 10 };
        rt.Children.Add(Ui.Text("실행 환경", "h2"));
        rt.Children.Add(_runtimeStatus);
        var install = Ui.Button("설치 / 업데이트", "primary");
        install.HorizontalAlignment = HorizontalAlignment.Left;
        install.Click += async (_, _) => await Install();
        var rosetta = Ui.Button("Rosetta 2 설치 열기", "ghost");
        rosetta.Click += (_, _) => WineManager.OpenRosettaInstaller();
        var rtButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        rtButtons.Children.Add(install);
        if (WineManager.NeedsRosetta) rtButtons.Children.Add(rosetta);
        rt.Children.Add(rtButtons);
        rt.Children.Add(_progress);
        rt.Children.Add(_progressText);
        root.Children.Add(Ui.Card(rt));

        // GPTK (optional)
        var gp = new StackPanel { Spacing = 10 };
        gp.Children.Add(Ui.Text("Game Porting Toolkit (선택 · DirectX 12)", "h2"));
        gp.Children.Add(Ui.Text("Apple에서 직접 받은 Game Porting Toolkit 실행 환경 폴더(bin/wine64 또는 bin/wine이 있는 폴더)를 지정하세요. SystemBoost는 이 파일을 배포하지 않아요.", "sub", wrap: true));
        gp.Children.Add(_gptkText);
        var pick = Ui.Button("폴더 지정", "ghost");
        pick.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var r = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Game Porting Toolkit 폴더" });
            var path = r.FirstOrDefault()?.TryGetLocalPath();
            if (path == null) return;
            _wine.GptkPath = path;
            Refresh();
        };
        var apple = Ui.Button("Apple 다운로드 페이지", "ghost");
        apple.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top != null) await top.Launcher.LaunchUriAsync(new Uri("https://developer.apple.com/games/game-porting-toolkit/"));
        };
        var gpb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        gpb.Children.Add(pick); gpb.Children.Add(apple);
        gp.Children.Add(gpb);
        root.Children.Add(Ui.Card(gp));

        // games
        var gm = new StackPanel { Spacing = 10 };
        gm.Children.Add(Ui.Text("게임", "h2"));
        gm.Children.Add(_games);
        gm.Children.Add(Ui.Row("그래픽 방식", "게임이 안 켜지거나 화면이 깨지면 다른 방식을 골라 보세요", _backend, first: true));
        var bar1 = new WrapPanel { Orientation = Orientation.Horizontal };
        Button B(string t, string cls, Action a)
        {
            var b = Ui.Button(t, cls);
            b.Margin = new Thickness(0, 0, 10, 10);
            b.Click += (_, _) => a();
            bar1.Children.Add(b);
            return b;
        }
        B("실행", "primary", Run);
        B("실행 + 프레임 생성", "primary", () => { Run(); _go("fg"); });
        B("설치 프로그램 실행 (.exe)", "ghost", async () =>
        {
            var exe = await PickExe("설치 프로그램 선택");
            if (exe == null) return;
            _status.Text = "설치 프로그램 실행 중… (창이 뜨면 안내대로 설치하세요)";
            try { var (code, _) = await _wine.RunInstallerAsync(exe, CancellationToken.None); _status.Text = $"설치 프로그램 종료 (코드 {code}). '설치된 게임 찾기'를 누르세요."; }
            catch (Exception e) { _status.Text = "오류: " + e.Message; }
        });
        B("Steam (Windows) 설치", "ghost", async () =>
        {
            try
            {
                _status.Text = "Steam 설치 파일 받는 중…";
                var setup = await _wine.DownloadSteamSetupAsync(CancellationToken.None);
                _status.Text = "Steam 설치 프로그램 실행 중…";
                await _wine.RunInstallerAsync(setup, CancellationToken.None);
                _status.Text = "끝나면 '설치된 게임 찾기'로 steam.exe를 추가하세요.";
            }
            catch (Exception e) { _status.Text = "오류: " + e.Message; }
        });
        B("게임 추가 (.exe)", "ghost", async () =>
        {
            var exe = await PickExe("게임 실행 파일 선택");
            if (exe != null) AddGame(exe);
        });
        B("설치된 게임 찾기", "ghost", () =>
        {
            int added = 0;
            foreach (var exe in _wine.FindInstalledExes())
                if (_wine.Games.All(g => g.ExePath != exe)) { AddGame(exe, save: false); added++; }
            _wine.SaveGames(); RefreshGames();
            _status.Text = added > 0 ? $"{added}개 추가했어요. 필요 없는 항목은 삭제하세요." : "새로 찾은 실행 파일이 없어요.";
        });
        B("삭제", "ghost", () =>
        {
            if (_games.SelectedItem is WineGame g) { _wine.Games.Remove(g); _wine.SaveGames(); RefreshGames(); }
        });
        gm.Children.Add(bar1);
        gm.Children.Add(_status);
        root.Children.Add(Ui.Card(gm));

        _games.SelectionChanged += (_, _) =>
        {
            if (_games.SelectedItem is WineGame g) _backend.SelectedIndex = g.Backend switch
            { GraphicsBackend.Dxvk => 0, GraphicsBackend.WineD3D => 1, _ => 2 };
        };
        _backend.SelectionChanged += (_, _) =>
        {
            if (_games.SelectedItem is not WineGame g || _backend.SelectedIndex < 0) return;
            g.Backend = _backend.SelectedIndex switch { 0 => GraphicsBackend.Dxvk, 1 => GraphicsBackend.WineD3D, _ => GraphicsBackend.D3DMetal };
            _wine.SaveGames();
        };

        Content = new ScrollViewer { Content = root };
        Refresh();
        RefreshGames();
    }

    private async System.Threading.Tasks.Task<string?> PickExe(string title)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;
        var r = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Windows 프로그램") { Patterns = new[] { "*.exe" } } },
        });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    private void AddGame(string exe, bool save = true)
    {
        _wine.Games.Add(new WineGame { Name = Path.GetFileNameWithoutExtension(exe), ExePath = exe });
        if (save) { _wine.SaveGames(); RefreshGames(); }
    }

    private void RefreshGames()
    {
        _games.ItemsSource = null;
        _games.ItemsSource = _wine.Games.ToList();
    }

    private async void Refresh()
    {
        bool rosetta = await WineManager.RosettaInstalledAsync();
        var (wine, dxvk) = WineManager.Manifest();
        _runtimeStatus.Text =
            (WineManager.NeedsRosetta ? $"Rosetta 2: {(rosetta ? "설치됨" : "필요해요 (Apple Silicon)")}\n" : "") +
            $"Wine: {(_wine.RuntimeInstalled ? "설치됨" : "설치 안 됨")}{(wine != null ? $" · 배포판 {wine.Tag}" : "")}\n" +
            $"DXVK: {(_wine.DxvkInstalled ? "설치됨" : dxvk == null ? "다운로드 정보 없음 (WineD3D로 실행)" : "설치 안 됨")}\n" +
            $"Windows 환경(보틀): {(_wine.BottleReady ? "준비됨" : "아직 없음")}";
        _gptkText.Text = _wine.GptkPath == null ? "지정 안 됨"
            : _wine.GptkWine != null ? $"사용 가능: {_wine.GptkPath}" : $"이 폴더에서 bin/wine을 찾지 못했어요: {_wine.GptkPath}";
    }

    private async System.Threading.Tasks.Task Install()
    {
        if (WineManager.NeedsRosetta && !await WineManager.RosettaInstalledAsync())
        {
            _progressText.Text = "먼저 Rosetta 2를 설치하세요 (버튼을 누르면 터미널에서 Apple 설치가 열려요).";
            return;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _progress.IsVisible = true;
        var prog = new Progress<(string step, double pct)>(p => { _progressText.Text = p.step; _progress.Value = p.pct; });
        try { await _wine.InstallAsync(prog, _cts.Token); }
        catch (Exception e) { _progressText.Text = "설치 실패: " + e.Message; }
        _progress.IsVisible = false;
        Refresh();
    }

    private void Run()
    {
        if (_games.SelectedItem is not WineGame g) { _status.Text = "게임을 먼저 고르세요"; return; }
        try
        {
            _wine.Launch(g);
            _status.Text = $"{g.Name} 실행 중 · 게임 창이 뜨면 프레임 생성 페이지에서 ↻를 눌러 그 창을 고르세요.";
        }
        catch (Exception e) { _status.Text = "오류: " + e.Message; }
    }
}
