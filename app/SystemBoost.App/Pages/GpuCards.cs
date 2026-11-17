// The GPU page's card for the picked graphics card (WindowsBoostBackend.GpuVendorAsync), the switches it cannot use,
// and the dashboard's graphics card tile.
//   NVIDIA  BoostExtras.GpuTuning, as before.
//   AMD     AmdTuning below: readings (every plan), Anti-Lag / Chill / Boost / Fluid Motion Frames status (every plan),
//           power limit and max clocks within ADLX's ranges and small raises per apply, auto tuning (Ultra),
//           factory reset (every plan). Laptops and APUs: tuning shown switched off, desktop graphics cards only.
//   Intel   no driver features yet.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SystemBoost.App.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace SystemBoost.App.Pages;

[SupportedOSPlatform("windows")]
internal static class GpuCards
{
    private const int PowerDropPct = 50, GpuPickMHz = 25, GpuDropMHz = 500, VramPickMHz = 10, VramDropMHz = 200;
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF7A1A"));
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// The tuning card of the GPU page. A PC whose NVIDIA card NVML reads gets the NVIDIA card at once, as before;
    /// otherwise GpuVendorAsync looks first (the plain helper for AMD, the engine's adapter list for Intel).
    public static Control Card(WindowsBoostBackend b, Func<Control> nvidia, TextBlock status)
    {
        if (WindowsBoostBackend.ReadGpu() != null) return Ui.Card(nvidia());   // GpuSelection: NVML reads it, so NVIDIA
        var holder = new ContentControl { Content = Ui.Text("그래픽카드를 확인하는 중…", "muted") };
        async void Fill()
        {
            var vendor = await b.GpuVendorAsync();
            holder.Content = vendor switch
            {
                GpuVendor.Amd => AmdTuning(b, status),
                GpuVendor.Intel => Intel(),
                _ => nvidia(),
            };
        }
        Fill();
        return Ui.Card(holder);
    }

    private static Control Intel()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("오버클럭 / 전력 제한 값", "h2"));
        p.Children.Add(Ui.Text(GpuCapabilities.IntelNotYet, "muted", wrap: true));
        return p;
    }

    /// The GPU page's switches this card cannot use stay visible, switched off, with the reason under them.
    /// One that is on stays usable, so it can always be turned back.
    public static async void GuardSwitches(WindowsBoostBackend b, IReadOnlyDictionary<string, (Border row, ToggleSwitch sw)> rows)
    {
        try
        {
            var caps = await b.GpuCapabilitiesAsync();
            await Guard("gpu.driver", caps.GameProfile, caps.GameProfileNote);
            await Guard("gpu.oc", caps.PowerLimit || caps.CoreClock || caps.MemClock, caps.TuningNote);
        }
        catch (Exception) { }

        async Task Guard(string id, bool supported, string? note)
        {
            if (supported || !rows.TryGetValue(id, out var r) || await b.IsAppliedAsync(id)) return;
            r.sw.IsEnabled = false;
            if (r.row.Child is Grid g && g.Children[0] is StackPanel left)
                left.Children.Add(Ui.Text(note ?? GpuCapabilities.NotOnThisCard, "muted", wrap: true));
        }
    }

    // ------------------------------------------------------------ AMD
    private static Control AmdTuning(WindowsBoostBackend b, TextBlock status)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("오버클럭 / 전력 제한 값", "h2"));
        if (b.AmdGpu is not { } first)
        {
            p.Children.Add(Ui.Text(b.AmdProblem ?? "AMD 드라이버에서 정보를 읽지 못했어요", "muted", wrap: true));
            p.Children.Add(Untested());
            return p;
        }
        var reading = Ui.Text(first.Name, "label", wrap: true);
        var values = Ui.Text("", "muted", wrap: true);
        var features = Ui.Text("", "muted", wrap: true);
        p.Children.Add(reading);
        p.Children.Add(values);
        p.Children.Add(features);

        var caps = AmdProtocol.Capabilities(first, b.AmdLaptop);
        if (caps.TuningNote is { } note)
        {
            var why = Ui.Text(note == GpuCapabilities.DesktopOnly
                ? "데스크톱 그래픽카드 전용이에요. 노트북과 내장 그래픽(APU)에서는 전력과 클럭을 바꾸지 않아요."
                : note, "label", wrap: true);
            why.Foreground = Orange;
            p.Children.Add(why);
        }

        var choice = b.AmdSettings;
        var power = new ComboBox { Width = 170 };
        var gpuMax = new ComboBox { Width = 170 };
        var vramMax = new ComboBox { Width = 170 };
        var temps = Enumerable.Range(AmdGpuChoice.MinTempLimitC, AmdGpuChoice.MaxTempLimitC - AmdGpuChoice.MinTempLimitC + 1).ToList();
        var temp = new ComboBox
        {
            Width = 170,
            ItemsSource = temps.Select(t => t == AmdGpuChoice.DefaultTempLimitC ? $"{t} °C (기본)" : $"{t} °C").ToArray(),
            SelectedIndex = Math.Max(0, temps.IndexOf(Math.Clamp(choice.TempLimitC, AmdGpuChoice.MinTempLimitC, AmdGpuChoice.MaxTempLimitC))),
        };
        var powerDesc = Ui.Text("", "muted", wrap: true);
        var gpuDesc = Ui.Text("", "muted", wrap: true);
        var vramDesc = Ui.Text("", "muted", wrap: true);
        p.Children.Add(PickerRow("전력 제한", powerDesc, power, true));
        p.Children.Add(PickerRow("GPU 최대 클럭", gpuDesc, gpuMax));
        p.Children.Add(PickerRow("VRAM 최대 클럭", vramDesc, vramMax));
        p.Children.Add(Ui.Row("제한 온도", $"GPU 온도가 이 값 이상으로 3초 이어지면 공장 초기값으로 돌려요 · 기본 {AmdGpuChoice.DefaultTempLimitC} °C", temp));

        // Who answers for what: the user for the values they choose; the seller still for its own program errors
        // (EULA Article 10: a clause that drops the seller's liability for intent or gross negligence is void).
        p.Children.Add(Ui.Text("주의: 오버클럭과 전력 제한은 사용자가 값을 골라 직접 적용하는 기능이에요. 적용한 뒤 생기는 게임 멈춤, 시스템 오류, "
                               + "그래픽카드 손상, 제조사 보증 문제는 사용자 책임이에요. 프로그램 자체의 오류로 생긴 손해는 사용권 계약 제10조를 따라요.",
                               "muted", wrap: true));
        var risk = new CheckBox { Content = "값을 높이면 게임이 멈추거나 화면이 꺼질 수 있고, 적용한 결과는 제 책임이라는 것을 이해했어요", IsChecked = b.AmdRiskAccepted };
        p.Children.Add(risk);
        var rule = Ui.Text("", "muted", wrap: true);
        p.Children.Add(rule);

        // what each picker offers comes from the card: ADLX's range, and small raises above the value on it now
        List<int> powers = new(), gpus = new(), vrams = new();
        bool filling = false;
        void Fill(AmdGpuInfo g)
        {
            filling = true;
            powers = AmdProtocol.Choices(g.Power, AmdProtocol.MaxPowerRaisePct, 1, PowerDropPct);
            gpus = AmdProtocol.Choices(g.GpuMax, AmdProtocol.MaxGpuRaiseMHz, GpuPickMHz, GpuDropMHz);
            vrams = AmdProtocol.Choices(g.VramMax, AmdProtocol.MaxVramRaiseMHz, VramPickMHz, VramDropMHz);
            Set(power, powers, g.Power, choice.PowerPct, v => (v == 0 ? "0% (기본)" : $"{v:+0;-0}%") + (v == g.Power.Now ? " · 지금" : ""));
            Set(gpuMax, gpus, g.GpuMax, choice.GpuMaxMHz, v => $"{v} MHz" + (v == g.GpuMax.Now ? " · 지금" : ""));
            Set(vramMax, vrams, g.VramMax, choice.VramMaxMHz, v => $"{v} MHz" + (v == g.VramMax.Now ? " · 지금" : ""));
            powerDesc.Text = g.Power.Ok ? $"낮추면 전력과 열이 줄고 성능도 줄 수 있어요 · 한 번에 +{AmdProtocol.MaxPowerRaisePct}%까지" : GpuCapabilities.NotOnThisCard;
            gpuDesc.Text = g.GpuMax.Ok ? $"한 번에 지금 값보다 +{AmdProtocol.MaxGpuRaiseMHz} MHz까지 올릴 수 있어요" : GpuCapabilities.NotOnThisCard;
            vramDesc.Text = g.VramMax.Ok ? $"한 번에 지금 값보다 +{AmdProtocol.MaxVramRaiseMHz} MHz까지 올릴 수 있어요" : GpuCapabilities.NotOnThisCard;
            var now = new List<string>();
            if (g.Power.Ok) now.Add($"전력 제한 {g.Power.Now:+0;-0;0}%");
            if (g.GpuMax.Ok) now.Add($"GPU 최대 {g.GpuMax.Now} MHz");
            if (g.VramMax.Ok) now.Add($"VRAM 최대 {g.VramMax.Now} MHz");
            values.Text = now.Count == 0 ? "" : "지금 값: " + string.Join(" · ", now) + (g.AtFactory ? " (공장 초기값)" : "");
            values.IsVisible = values.Text.Length > 0;
            filling = false;
            // what is kept is what is shown: a choice the card no longer offers falls back to the value on it now
            if ((choice.PowerPct, choice.GpuMaxMHz, choice.VramMaxMHz) != (Picked(power, powers, g.Power), Picked(gpuMax, gpus, g.GpuMax), Picked(vramMax, vrams, g.VramMax)))
                Store();
        }
        int? Picked(ComboBox box, List<int> list, AmdRange r) =>
            r.Ok && box.SelectedIndex >= 0 && box.SelectedIndex < list.Count && list[box.SelectedIndex] != r.Now ? list[box.SelectedIndex] : null;
        void Store()
        {
            if (filling || b.AmdGpu is not { } g) return;
            b.SetAmdChoice(Picked(power, powers, g.Power), Picked(gpuMax, gpus, g.GpuMax), Picked(vramMax, vrams, g.VramMax),
                           temps[Math.Max(0, temp.SelectedIndex)], risk.IsChecked == true);
        }
        power.SelectionChanged += (_, _) => Store();
        gpuMax.SelectionChanged += (_, _) => Store();
        vramMax.SelectionChanged += (_, _) => Store();
        temp.SelectionChanged += (_, _) => Store();
        risk.IsCheckedChanged += (_, _) => Store();

        var autoNote = Ui.Text("", "muted", wrap: true);
        var keepText = Ui.Text("", "label", wrap: true);
        var keepBox = new StackPanel { Spacing = 8, IsVisible = false };
        var reset = Ui.Button("공장 초기값으로 복구", "ghost");
        var resetNote = Ui.Text("모든 플랜에서 쓸 수 있어요. AMD Software에서 직접 맞춘 전력·클럭 값도 공장 초기값으로 돌아가요.", "muted", wrap: true);

        // ADLX auto tuning, through the same confirm window
        var auto = new StackPanel { Spacing = 8 };
        var autoHead = new StackPanel { Orientation = Orientation.Horizontal };
        autoHead.Children.Add(Ui.Text("자동 튜닝", "label"));
        autoHead.Children.Add(Ui.Badge(License.PlanOf(PremiumFeature.Overclock)));
        auto.Children.Add(autoHead);
        auto.Children.Add(Ui.Text($"AMD 드라이버의 자동 튜닝을 써요. 끝날 때까지 1분쯤 걸릴 수 있고, 끝난 뒤에도 '유지'를 눌러야 남아요. 제한 온도는 {AmdGpuChoice.DefaultTempLimitC} °C로 정해져 있어요. 위 체크가 필요해요.", "muted", wrap: true));
        // amd.tuning.revert sets power and max clocks only: an undervolt's voltage goes back with a factory reset
        auto.Children.Add(Ui.Text("공장 초기값이 아닌 상태에서 자동 튜닝을 쓰면, 되돌릴 때 전압 값은 그대로 남을 수 있어요. 그럴 때는 '공장 초기값으로 복구'를 눌러 주세요.", "muted", wrap: true));
        var autoRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var autoButtons = new List<(Button button, GpuAutoTuning kind)>();
        foreach (var kind in new[] { GpuAutoTuning.Undervolt, GpuAutoTuning.OverclockGpu, GpuAutoTuning.OverclockVram })
        {
            var button = Ui.Button(AmdGpuBackend.AutoTitle(kind), "ghost");
            button.Margin = new Thickness(0, 0, 10, 6);
            button.Click += async (_, _) =>
            {
                if (!License.Allowed(PremiumFeature.Overclock))
                {
                    status.Text = $"{AmdGpuBackend.AutoTitle(kind)}은(는) {License.PlanOf(PremiumFeature.Overclock)} 기능이에요. 왼쪽 '★ 플랜'에서 열 수 있어요.";
                    return;
                }
                foreach (var (other, _) in autoButtons) other.IsEnabled = false;
                status.Text = $"{AmdGpuBackend.AutoTitle(kind)}: 처리하는 중…";
                status.Text = $"{AmdGpuBackend.AutoTitle(kind)}: {await b.AmdAutoTuneAsync(kind)}";
                Refresh();
            };
            autoButtons.Add((button, kind));
            autoRow.Children.Add(button);
        }
        auto.Children.Add(autoRow);
        auto.Children.Add(autoNote);
        p.Children.Add(auto);

        var keep = Ui.Button("유지", "primary");
        keep.Click += async (_, _) => status.Text = "오버클럭 / 전력 제한: " + await b.KeepAmdTuningAsync();
        keepBox.Children.Add(keepText);
        keepBox.Children.Add(keep);
        p.Children.Add(keepBox);

        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Click += async (_, _) =>
        {
            reset.IsEnabled = false;
            status.Text = "공장 초기값으로 복구: " + await b.AmdResetAsync();
            Refresh();
        };
        p.Children.Add(reset);
        p.Children.Add(resetNote);
        p.Children.Add(Untested());

        int ticks = 0;
        void Refresh()
        {
            var g = b.AmdGpu ?? first;
            var c = AmdProtocol.Capabilities(g, b.AmdLaptop);
            bool tuned = b.AmdTuned;
            rule.Text = $"위 스위치를 켜면 적용돼요. {AmdGpuBackend.ConfirmSeconds}초 안에 '유지'를 누르지 않거나 앱을 끄면 이전 값으로, "
                        + $"GPU가 {temps[Math.Max(0, temp.SelectedIndex)]} °C 이상이 되거나 온도를 읽지 못하거나 드라이버 오류가 나면 공장 초기값으로 돌아가요. "
                        + "제조사 보증 조건은 직접 확인해 주세요.";
            power.IsEnabled = c.PowerLimit && !tuned;
            gpuMax.IsEnabled = c.CoreClock && !tuned;
            vramMax.IsEnabled = c.MemClock && !tuned;
            temp.IsEnabled = c.AnyTuning && !tuned;
            risk.IsEnabled = c.AnyTuning;
            foreach (var (button, kind) in autoButtons) button.IsEnabled = c.Auto(kind) && !tuned;
            var missing = autoButtons.Where(x => !c.Auto(x.kind)).Select(x => AmdGpuBackend.AutoTitle(x.kind)).ToList();
            autoNote.Text = c.TuningNote == null && missing.Count > 0 ? $"{string.Join(" · ", missing)}: {GpuCapabilities.NotOnThisCard}" : "";
            autoNote.IsVisible = autoNote.Text.Length > 0;
            reset.IsEnabled = c.ResetToFactory;
            resetNote.IsVisible = c.ResetToFactory;
            var left = b.AmdConfirmLeft;
            keepBox.IsVisible = left != null;
            keepText.Text = $"화면과 게임이 정상이면 {left}초 안에 '유지'를 눌러 주세요. 누르지 않으면 원래대로 돌아가요.";
        }
        async Task Look()
        {
            if (ticks++ % 5 == 0)   // the card's values and switches every 5 seconds, the readings every second
            {
                var info = await b.ReadAmdAsync();
                if (b.AmdGpu is { } g && info.Status == "ok" && !b.AmdTuned) Fill(g);
                features.Text = await b.ReadAmdProfileAsync() is { } s
                    ? $"Anti-Lag {AmdProtocol.SwitchText(s.AntiLag)} · Radeon Chill {AmdProtocol.SwitchText(s.Chill)} · Radeon Boost {AmdProtocol.SwitchText(s.Boost)} · AMD Fluid Motion Frames {AmdProtocol.SwitchText(s.Afmf)}"
                    : "";
                features.IsVisible = features.Text.Length > 0;
            }
            var t = await b.ReadAmdTelemetryAsync();
            reading.Text = (b.AmdGpu ?? first).Name + (t != null ? Telemetry(t) : "");
            Refresh();
        }
        Fill(first);
        Refresh();
        features.IsVisible = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        bool busy = false;
        timer.Tick += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            try { await Look(); }
            catch (Exception) { }
            finally { busy = false; }
        };
        b.Changed += () => Dispatcher.UIThread.Post(Refresh);
        p.AttachedToVisualTree += (_, _) => { ticks = 0; timer.Start(); };
        p.DetachedFromVisualTree += (_, _) => timer.Stop();
        return p;
    }

    private static Control Untested()
    {
        var t = Ui.Text(AmdGpuBackend.Untested, "muted", wrap: true);
        t.Foreground = Orange;
        return t;
    }

    private static Border PickerRow(string title, TextBlock desc, ComboBox box, bool first = false)
    {
        var row = Ui.Row(title, "", box, first);
        if (row.Child is Grid g && g.Children[0] is StackPanel left) left.Children.Add(desc);
        return row;
    }

    // An unsupported value is shown as a switched-off picker with the note, not hidden.
    private static void Set(ComboBox box, List<int> values, AmdRange r, int? chosen, Func<int, string> label)
    {
        if (!r.Ok)
        {
            box.ItemsSource = new[] { "—" };
            box.SelectedIndex = 0;
            return;
        }
        box.ItemsSource = values.Select(label).ToArray();
        int at = chosen is int c ? values.IndexOf(c) : -1;
        box.SelectedIndex = at >= 0 ? at : values.IndexOf(r.Now);
    }

    /// " · 62 °C · GPU 2430 MHz · ..." for what the driver reports.
    public static string Telemetry(GpuTelemetry t)
    {
        var parts = new List<string>();
        if (t.TempC >= 0) parts.Add($"{t.TempC} °C");
        if (t.HotspotC >= 0) parts.Add($"핫스폿 {t.HotspotC} °C");
        if (t.CoreMHz >= 0) parts.Add($"GPU {t.CoreMHz} MHz");
        if (t.MemMHz >= 0) parts.Add($"VRAM {t.MemMHz} MHz");
        if (t.PowerW >= 0) parts.Add($"{t.PowerW} W");
        if (t.UsagePct >= 0) parts.Add($"사용률 {t.UsagePct}%");
        if (t.VramUsedMB >= 0) parts.Add($"VRAM 사용 {t.VramUsedMB.ToString("N0", Inv)} MB");
        return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
    }

    // ------------------------------------------------------------ dashboard
    /// The dashboard's graphics card tile: the card's name and its readings, free on every plan.
    public static Control Tile(WindowsBoostBackend b, Action<string> go)
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Ui.Text("그래픽카드", "h2"));
        var name = Ui.Text("확인하는 중…", "label", wrap: true);
        var reading = Ui.Text("", "muted", wrap: true);
        var untested = Untested();
        reading.IsVisible = untested.IsVisible = false;
        p.Children.Add(name);
        p.Children.Add(reading);
        p.Children.Add(untested);
        var open = Ui.Button("열기", "primary");
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Click += (_, _) => go("gpu");
        p.Children.Add(open);
        async Task Look()
        {
            var (vendor, n, t) = await b.GpuTileAsync();
            name.Text = n.Length > 0 ? n : "그래픽카드 이름을 읽지 못했어요";
            reading.Text = t != null ? Telemetry(t).TrimStart(' ', '·') : "";
            reading.IsVisible = reading.Text.Length > 0;
            untested.IsVisible = vendor == GpuVendor.Amd;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        bool busy = false;
        async void Tick()
        {
            if (busy) return;
            busy = true;
            try { await Look(); }
            catch (Exception) { }
            finally { busy = false; }
        }
        timer.Tick += (_, _) => Tick();
        p.AttachedToVisualTree += (_, _) => { Tick(); timer.Start(); };
        p.DetachedFromVisualTree += (_, _) => timer.Stop();
        return p;
    }
}
