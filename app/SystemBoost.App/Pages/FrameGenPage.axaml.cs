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
            if (v is "x6" or "x8" && !License.Allowed(PremiumFeature.UltraMultiplier)) return LockedUltra("6배·8배는");
            if (v is "auto" or "x3" or "x4" && !License.Allowed(PremiumFeature.AutoAndHighMultiplier)) return Locked("자동·3배·4배");
            vm.Mode = v; return true;
        });
        Segment(MaxSeg, vm.MaxMul.ToString(CultureInfo.InvariantCulture), v =>
        {
            if (v is "6" or "8" && !License.Allowed(PremiumFeature.UltraMultiplier)) return LockedUltra("6배·8배는");
            if (v != "2" && !License.Allowed(PremiumFeature.AutoAndHighMultiplier)) return Locked("자동 최대 3·4배");
            vm.MaxMul = int.Parse(v, CultureInfo.InvariantCulture); return true;
        });
        Segment(QualitySeg, vm.Quality, v =>
        {
            if (v == "quality" && !License.Allowed(PremiumFeature.QualityMode)) return Locked("품질 모드");
            vm.Quality = v; return true;
        });
        Segment(CaptureSeg, vm.Capture, v => { vm.Capture = v; return true; });
        Segment(TargetSeg, vm.TargetFps.ToString(CultureInfo.InvariantCulture), v =>
        {
            vm.TargetFps = int.Parse(v, CultureInfo.InvariantCulture);
            TargetBox.Text = "";
            return true;
        });
        TargetBox.LostFocus += (_, _) => ApplyTarget();
        TargetBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) ApplyTarget(); };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Mode)) Select(ModeSeg, vm.Mode);
            if (e.PropertyName == nameof(vm.MaxMul)) Select(MaxSeg, vm.MaxMul.ToString(CultureInfo.InvariantCulture));
            if (e.PropertyName == nameof(vm.Quality)) Select(QualitySeg, vm.Quality);
            if (e.PropertyName == nameof(vm.SelectedGpu) && vm.SelectedGpu is { Value: not "same" } &&
                !License.Allowed(PremiumFeature.DualGpu))
            {
                LockedUltra("듀얼 GPU는");
                vm.SelectedGpu = vm.Gpus.FirstOrDefault();
            }
        };

        StartButton.Click += (_, _) => vm.Toggle();
        RefreshButton.Click += async (_, _) => await vm.RefreshAsync();
        ApplyButton.Click += (_, _) => vm.Restart();
        DefaultsButton.Click += (_, _) =>
        {
            vm.DlssMode = false;
            vm.Mode = FrameGenViewModel.DefaultMode; vm.MaxMul = FrameGenViewModel.DefaultMaxMul; vm.Quality = "balanced"; vm.Upscale = false; vm.Sharpness = 0.5; vm.Capture = "wgc"; vm.TargetFps = 0;
            TargetBox.Text = ""; Select(TargetSeg, "0");
            Select(ModeSeg, vm.Mode); Select(MaxSeg, vm.MaxMul.ToString(CultureInfo.InvariantCulture)); Select(QualitySeg, "balanced"); Select(CaptureSeg, "wgc");
        };
        vm.HistoryChanged += () => Chart.SetData(vm.History);

        NoteText.Text =
            "· 보간은 다음 프레임을 기다려서 지연이 늘어나요(원본 1프레임 + 처리 시간). 경쟁 게임에는 권장하지 않아요.\n" +
            "· 6배·8배와 듀얼 GPU는 울트라 플랜 전용이에요.\n" +
            "· '출력 프레임'을 고르지 않으면 모니터 주사율까지 나와요. 모니터는 주사율만큼만 보여 줘서, 그보다 높게 고른 프레임은 표시되지 않고 GPU만 더 써요.\n" +
            "· 프레임 생성에는 GPU 여유가 필요해요. 게임이 GPU를 다 쓰면 출력이 원본보다 적어질 수 있어서, 그때는 생성을 멈추고 게임 화면을 그대로 보여 줘요. 게임 프레임을 제한하면 여유가 생기고 결과도 더 고르게 나와요. 그래픽 카드 사양이 낮으면 게임 프레임을 지금의 4분의 3쯤으로 제한하고 2배부터 시작해 보세요.\n" +
            "· 듀얼 GPU는 켜기 전후로 벤치마크해서 효과가 있을 때만 쓰세요.\n" +
            (Platform.IsMac
                ? "· macOS: 처음 실행할 때 '화면 기록' 권한이 필요해요. 업스케일 중에는 실제 커서가 게임 창 안에 머물러요."
                : "· 게임은 테두리 없는 창 또는 창 모드로 실행하세요.");
        if (Platform.IsMac) UpscaleHint.Text = "화면 비율 유지 · 마우스 커서가 있는 게임은 커서 위치가 다르게 보일 수 있어요";

        // a game started after the last look has to reach the list without the user knowing about ↻
        AttachedToVisualTree += async (_, _) => { if (!vm.Running) await vm.RefreshAsync(); };
        WindowBox.DropDownOpened += async (_, _) => { if (!vm.Running) await vm.RefreshAsync(); };
    }

    /// A number typed in the box: Enter or leaving it. Empty leaves the choice as it is.
    private void ApplyTarget()
    {
        var text = TargetBox.Text?.Trim() ?? "";
        if (text.Length == 0) return;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int fps) ||
            fps < FrameGenViewModel.MinTargetFps || fps > FrameGenViewModel.MaxTargetFps)
        {
            _vm.Notice($"출력 프레임은 {FrameGenViewModel.MinTargetFps}에서 {FrameGenViewModel.MaxTargetFps} 사이 숫자로 적어 주세요.");
            TargetBox.Text = "";
            return;
        }
        _vm.TargetFps = fps;
        Select(TargetSeg, fps.ToString(CultureInfo.InvariantCulture));
    }

    private bool Locked(string what)
    {
        _vm.Notice($"{what}은(는) 프리미엄 기능이에요. 왼쪽 '★ 프리미엄'에서 열 수 있어요.");
        return false;
    }

    private bool LockedUltra(string what)
    {
        _vm.Notice($"{what} 울트라 플랜 전용이에요. 왼쪽 '★ 프리미엄'에서 확인하세요.");
        return false;
    }

    /// onPick returns false to refuse the choice (selection snaps back).
    private static void Segment(StackPanel group, string current, Func<string, bool> onPick)
    {
        group.Tag = current;
        foreach (var t in group.Children.OfType<ToggleButton>())
        {
            t.IsChecked = (string?)t.Tag == current;
            t.Click += (s, _) =>
            {
                var picked = (ToggleButton)s!;
                if (onPick((string)picked.Tag!)) group.Tag = picked.Tag;
                Select(group, (string)group.Tag!);
            };
        }
    }

    private static void Select(StackPanel group, string value)
    {
        group.Tag = value;   // a refused pick snaps back to this
        foreach (var t in group.Children.OfType<ToggleButton>()) t.IsChecked = (string?)t.Tag == value;
    }
}
