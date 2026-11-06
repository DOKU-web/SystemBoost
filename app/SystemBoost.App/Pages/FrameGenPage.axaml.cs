using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using SystemBoost.App.Services;
using SystemBoost.App.ViewModels;
using System;
using System.Globalization;
using System.Linq;

namespace SystemBoost.App.Pages;

public partial class FrameGenPage : UserControl
{
    private readonly FrameGenViewModel _vm;

    public FrameGenPage() : this(new FrameGenViewModel()) { }   // designer

    public FrameGenPage(FrameGenViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        Segment(ModeSeg, vm.Mode, v =>
        {
            if (v is "x6" or "x8" && !License.Allowed(PremiumFeature.UltraMultiplier)) return LockedUltra();
            if (v is "auto" or "x3" or "x4" && !License.Allowed(PremiumFeature.AutoAndHighMultiplier)) return Locked("자동·3배·4배");
            vm.Mode = v; return true;
        });
        Segment(MaxSeg, vm.MaxMul.ToString(CultureInfo.InvariantCulture), v =>
        {
            if (v is "6" or "8" && !License.Allowed(PremiumFeature.UltraMultiplier)) return LockedUltra();
            if (v != "2" && !License.Allowed(PremiumFeature.AutoAndHighMultiplier)) return Locked("자동 최대 3·4배");
            vm.MaxMul = int.Parse(v, CultureInfo.InvariantCulture); return true;
        });
        Segment(QualitySeg, vm.Quality, v =>
        {
            if (v == "quality" && !License.Allowed(PremiumFeature.QualityMode)) return Locked("품질 모드");
            vm.Quality = v; return true;
        });
        Segment(CaptureSeg, vm.Capture, v => { vm.Capture = v; return true; });
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedGpu) && vm.SelectedGpu is { Value: not "same" } &&
                !License.Allowed(PremiumFeature.DualGpu))
            {
                Locked("듀얼 GPU");
                vm.SelectedGpu = vm.Gpus.FirstOrDefault();
            }
        };

        StartButton.Click += (_, _) => vm.Toggle();
        RefreshButton.Click += async (_, _) => await vm.RefreshAsync();
        ApplyButton.Click += (_, _) => vm.Restart();
        DefaultsButton.Click += (_, _) =>
        {
            vm.Mode = FrameGenViewModel.DefaultMode; vm.MaxMul = FrameGenViewModel.DefaultMaxMul; vm.Quality = "balanced"; vm.Upscale = false; vm.Sharpness = 0.5; vm.Capture = "wgc";
            Select(ModeSeg, vm.Mode); Select(MaxSeg, vm.MaxMul.ToString(CultureInfo.InvariantCulture)); Select(QualitySeg, "balanced"); Select(CaptureSeg, "wgc");
        };
        vm.HistoryChanged += () => Chart.SetData(vm.History);

        NoteText.Text =
            "· 보간은 다음 프레임을 기다려서 지연이 늘어나요(원본 1프레임 + 처리 시간). 경쟁 게임에는 권장하지 않아요.\n" +
            "· 6배·8배는 울트라 플랜 전용이에요. 출력 FPS는 모니터 주사율을 넘지 않아서 게임 FPS가 낮을 때만 차이가 나요.\n" +
            "· 게임 FPS를 일정하게 제한하면 결과가 더 고르게 나와요.\n" +
            "· 듀얼 GPU는 켜기 전후로 벤치마크해서 효과가 있을 때만 쓰세요.\n" +
            (Platform.IsMac
                ? "· macOS: 처음 실행할 때 '화면 기록' 권한이 필요해요. 업스케일 중에는 실제 커서가 게임 창 안에 머물러요."
                : "· 게임은 테두리 없는 창 또는 창 모드로 실행하세요.");
        if (Platform.IsMac) UpscaleHint.Text = "화면 비율 유지 · 마우스 커서가 있는 게임은 커서 위치가 다르게 보일 수 있어요";

        AttachedToVisualTree += async (_, _) => { if (vm.Windows.Count == 0) await vm.RefreshAsync(); };
    }

    private bool Locked(string what)
    {
        _vm.Notice($"{what}은(는) 프리미엄 기능이에요. 왼쪽 '★ 프리미엄'에서 열 수 있어요.");
        return false;
    }

    private bool LockedUltra()
    {
        _vm.Notice("6배·8배는 울트라 플랜 전용이에요. 왼쪽 '★ 프리미엄'에서 확인하세요.");
        return false;
    }

    /// onPick returns false to refuse the choice (selection snaps back).
    private static void Segment(StackPanel group, string current, Func<string, bool> onPick)
    {
        string selected = current;
        foreach (var t in group.Children.OfType<ToggleButton>())
        {
            t.IsChecked = (string?)t.Tag == current;
            t.Click += (s, _) =>
            {
                var picked = (ToggleButton)s!;
                if (onPick((string)picked.Tag!)) selected = (string)picked.Tag!;
                foreach (var o in group.Children.OfType<ToggleButton>()) o.IsChecked = (string?)o.Tag == selected;
            };
        }
    }

    private static void Select(StackPanel group, string value)
    {
        foreach (var t in group.Children.OfType<ToggleButton>()) t.IsChecked = (string?)t.Tag == value;
    }
}
