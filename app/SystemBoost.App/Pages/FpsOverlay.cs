// FPS in a corner of the screen while a game is in front (Windows). The numbers come from
// WindowsBoostBackend.FpsLiveReadAsync: Windows event tracing, the game is never opened or touched.
// The display is a separate small window that clicks pass through and that never takes the focus.
// It is never hidden from capture (SetWindowDisplayAffinity): anti-cheat programs read a capture-hidden
// window over a game as a cheat overlay, so it shows in screenshots and broadcasts like any window.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SystemBoost.App.Services;
using SystemBoost.App.ViewModels;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SystemBoost.App.Pages;

[SupportedOSPlatform("windows")]
public static class FpsOverlay
{
    public static readonly string[] Corners = { "왼쪽 위", "오른쪽 위", "왼쪽 아래", "오른쪽 아래" };
    private const int TickMs = 500;
    private const double ShowAfterSeconds = 2;   // drawn this long without a pause: a short burst of an ordinary app stays quiet

    // ------------------------------------------------------------ settings
    private sealed record Prefs(bool On = false, int Corner = 0);
    private static string PrefsFile => Path.Combine(Platform.DataDir, "fps-overlay.json");
    private static Prefs Load()
    {
        try { return File.Exists(PrefsFile) ? JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsFile)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }
    private static void Store(Prefs p)
    {
        try { File.WriteAllText(PrefsFile, JsonSerializer.Serialize(p)); } catch (Exception) { }
    }
    public static bool On => Load().On;
    public static int Corner
    {
        get => Math.Clamp(Load().Corner, 0, Corners.Length - 1);
        set => Store(Load() with { Corner = Math.Clamp(value, 0, Corners.Length - 1) });
    }

    /// For the dashboard card; raised on the UI thread.
    public static event Action? Changed;
    public static string Status { get; private set; } = "꺼져 있어요";
    private static void SetStatus(string text) { Status = text; Changed?.Invoke(); }

    // ------------------------------------------------------------ running
    private static WindowsBoostBackend? _backend;
    private static FrameGenViewModel? _frameGen;
    private static CancellationTokenSource? _run;
    private static FpsOverlayWindow? _window;
    private static uint _shownPid;

    /// Once at app start, on the UI thread: starts after the agreement if it was left on.
    public static async void Init(WindowsBoostBackend backend, FrameGenViewModel frameGen)
    {
        _backend = backend;
        _frameGen = frameGen;
        await Eula.WhenAccepted;
        if (On) await RunAsync();
    }

    public static async Task SetOnAsync(bool on)
    {
        if (on == (_run != null)) return;
        Store(Load() with { On = on });
        if (on) await RunAsync();
        else await StopAsync("꺼져 있어요");
    }

    private static async Task RunAsync()
    {
        if (_run != null || _backend == null) return;
        var run = _run = new CancellationTokenSource();
        SetStatus("FPS 측정을 준비하는 중… (관리자 권한을 물어볼 수 있어요)");
        var error = await _backend.FpsLiveStartAsync();
        if (run.IsCancellationRequested)   // turned off while Windows was asking
        {
            if (error == null && _run == null) await _backend.FpsLiveStopAsync();
            return;
        }
        if (error != null)
        {
            Store(Load() with { On = false });   // asked once; the next app start does not ask again
            _run = null;
            SetStatus(error);
            return;
        }
        SetStatus("켜져 있어요 · 게임 화면이 앞에 있으면 구석에 떠요");
        _window ??= new FpsOverlayWindow();
        while (!run.IsCancellationRequested)
        {
            if (!_backend.FpsLiveRunning) { await StopAsync("FPS 측정이 멈췄어요. 다시 켜 주세요"); Store(Load() with { On = false }); return; }
            try { await TickAsync(); }
            catch (Exception) { _window?.Hide(); }
            try { await Task.Delay(TickMs, run.Token); } catch (TaskCanceledException) { }
        }
    }

    private static async Task StopAsync(string status)
    {
        _run?.Cancel();
        _run = null;
        _window?.Hide();
        _shownPid = 0;
        if (_backend != null) await _backend.FpsLiveStopAsync();
        SetStatus(status);
    }

    /// App exit: the window goes; the helper stops counting as it exits with the app.
    public static void Shutdown()
    {
        _run?.Cancel();
        _run = null;
        _window?.Dispose();
        _window = null;
    }

    private static async Task TickAsync()
    {
        var front = GetForegroundWindow();
        uint pid = 0;
        if (front != IntPtr.Zero) GetWindowThreadProcessId(front, out pid);
        GetWindowThreadProcessId(GetShellWindow(), out var shell);
        if (pid == 0 || pid == Environment.ProcessId || pid == shell) { Hide(); return; }   // desktop, taskbar, File Explorer, this app
        var read = await _backend!.FpsLiveReadAsync((int)pid);
        if (read is not { } r || (pid != _shownPid && r.Steady < ShowAfterSeconds) || _run == null) { Hide(); return; }
        string big, small;
        if (_frameGen is { Running: true } fg && int.TryParse(fg.OutFps, out var output) && output > 0)
        { big = output.ToString(); small = Lang.T($"원본 {r.Fps:0}"); }
        else { big = r.Fps.ToString("0"); small = $"1% {r.Low:0}"; }
        _window?.Show(front, Corner, big, small);
        _shownPid = pid;
    }

    private static void Hide()
    {
        _window?.Hide();
        _shownPid = 0;
    }

    // ------------------------------------------------------------ dashboard card
    public static Control Card()
    {
        var p = new StackPanel { Spacing = 10 };
        var toggle = new ToggleSwitch { OnContent = "", OffContent = "", IsChecked = _run != null || On };
        Avalonia.Automation.AutomationProperties.SetName(toggle, "FPS 표시");
        var list = new StackPanel();
        list.Children.Add(Ui.Row("FPS 표시", "게임 화면이 앞에 있는 동안 화면 구석에 FPS를 작게 띄워요. 켜 두면 앱을 켤 때 함께 시작해요.", toggle, first: true));
        var corner = new ComboBox { ItemsSource = Corners, SelectedIndex = Corner, Width = 150 };
        corner.SelectionChanged += (_, _) => { if (corner.SelectedIndex >= 0) Corner = corner.SelectedIndex; };
        list.Children.Add(Ui.Row("위치", "", corner));
        p.Children.Add(list);
        var status = Ui.Text(Status, "label", wrap: true);
        p.Children.Add(status);
        p.Children.Add(Ui.Text("큰 숫자는 지난 1초의 FPS, 아래 줄은 최근 10초에서 가장 느린 1% 프레임으로 잰 1% 낮은 값이에요. " +
                               "프레임 생성 중에는 큰 숫자가 출력 FPS, 아래 줄이 게임의 원본 FPS예요.", "muted", wrap: true));
        p.Children.Add(Ui.Text("Windows가 기록하는 화면 출력 횟수로 재요 (PresentMon, Xbox Game Bar와 같은 방식). 게임 파일과 메모리는 건드리지 않아요. " +
                               "이 기록은 관리자 권한이 있어야 읽을 수 있어서 켤 때 관리자 권한을 물어봐요 (앱을 켤 때마다 한 번).", "muted", wrap: true));
        p.Children.Add(Ui.Text("FPS 창은 클릭이 통과하는 별도 창이라 화면 캡처와 방송에도 보여요. 전체 화면 전용 모드 게임에서는 안 보일 수 있으니 테두리 없는 창 모드를 쓰세요.", "muted", wrap: true));
        bool setting = false;
        toggle.IsCheckedChanged += async (_, _) => { if (!setting) await SetOnAsync(toggle.IsChecked == true); };
        void Refresh()
        {
            status.Text = Status;
            setting = true;
            toggle.IsChecked = _run != null;
            setting = false;
        }
        Changed += Refresh;
        p.DetachedFromVisualTree += (_, _) => Changed -= Refresh;
        p.AttachedToVisualTree += (_, _) => { Changed -= Refresh; Changed += Refresh; Refresh(); };
        return p;
    }

    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern IntPtr GetShellWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

/// The small window itself: a layered pop-up drawn with Avalonia into a bitmap and handed to Windows with
/// UpdateLayeredWindow, so its edges are see-through. Clicks pass through (WS_EX_TRANSPARENT), it never
/// takes the focus (WS_EX_NOACTIVATE) and stays out of Alt+Tab (WS_EX_TOOLWINDOW).
[SupportedOSPlatform("windows")]
internal sealed class FpsOverlayWindow : IDisposable
{
    private const string ClassName = "SystemBoostFps";
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF7A1A"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#C8C8C8"));
    private static readonly IBrush Back = new SolidColorBrush(Color.FromArgb(0xC8, 0x0B, 0x0B, 0x0B));
    private static readonly FontFamily Font = new("Malgun Gothic, Segoe UI");

    private IntPtr _hwnd;
    private bool _shown;
    private string _drawn = "";

    public IntPtr Handle => _hwnd;

    public FpsOverlayWindow()
    {
        var instance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            hInstance = instance,
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);   // fails harmlessly when it is already registered
        _hwnd = CreateWindowEx(WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
                               ClassName, "SystemBoost FPS", WsPopup, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    /// Shows the numbers in a corner of the monitor the game window is on.
    public void Show(IntPtr game, int corner, string big, string small)
    {
        if (_hwnd == IntPtr.Zero) return;
        var monitor = MonitorFromWindow(game, 2);   // nearest
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        double scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        var key = $"{big}|{small}|{corner}|{scale}|{info.rcMonitor.left},{info.rcMonitor.top},{info.rcMonitor.right},{info.rcMonitor.bottom}";
        if (key != _drawn && Draw(big, small, corner, scale, info.rcMonitor)) _drawn = key;
        if (!_shown) { ShowWindow(_hwnd, 4); _shown = true; }   // SW_SHOWNOACTIVATE
        // above a frame generation overlay that opened later; never activates
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200);
    }

    public void Hide()
    {
        if (!_shown || _hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, 0);
        _shown = false;
    }

    private bool Draw(string big, string small, int corner, double scale, RECT monitor)
    {
        var number = new TextBlock { Text = big, FontFamily = Font, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Orange, MinWidth = 38 };
        var unit = new TextBlock { Text = "FPS", FontFamily = Font, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Gray,
                                   VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 4) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(number);
        top.Children.Add(unit);
        var lines = new StackPanel();
        lines.Children.Add(top);
        lines.Children.Add(new TextBlock { Text = small, FontFamily = Font, FontSize = 11, Foreground = Gray, Margin = new Thickness(0, -2, 0, 0) });
        var box = new Border { Background = Back, CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 3, 10, 5), Child = lines };
        box.Measure(Size.Infinity);
        box.Arrange(new Rect(box.DesiredSize));
        int w = Math.Max(1, (int)Math.Ceiling(box.DesiredSize.Width * scale));
        int h = Math.Max(1, (int)Math.Ceiling(box.DesiredSize.Height * scale));
        int margin = (int)Math.Round(12 * scale);
        int x = corner is 0 or 2 ? monitor.left + margin : monitor.right - w - margin;
        int y = corner is 0 or 1 ? monitor.top + margin : monitor.bottom - h - margin;

        using var bitmap = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96 * scale, 96 * scale));
        bitmap.Render(box);
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        bool ok = false;
        if (dib != IntPtr.Zero)
        {
            var old = SelectObject(memory, dib);
            bitmap.CopyPixels(new PixelRect(0, 0, w, h), bits, w * h * 4, w * 4);   // premultiplied BGRA, what Windows takes
            var at = new POINT { x = x, y = y };
            var size = new SIZE { cx = w, cy = h };
            var from = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            ok = UpdateLayeredWindow(_hwnd, screen, ref at, ref size, memory, ref from, 0, ref blend, 2);
            SelectObject(memory, old);
            DeleteObject(dib);
        }
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
        return ok;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT at, ref SIZE size, IntPtr src, ref POINT from,
                                                   int key, ref BLENDFUNCTION blend, int flags);
}
