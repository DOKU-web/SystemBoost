// The overclocking & power limit notice (Services/OverclockNotice.cs) in a window, black with the orange accent like EulaDialog.
//   ask: true   OcConsent.EnsureAsync. ShowDialog<bool> is true only from '동의하고 켜기', which needs the box ticked and
//               WaitSeconds since the window opened (the countdown is on the button). '취소' is the default and the cancel
//               button and has the focus. Enter never agrees: the window catches it before any control (also on the agree
//               button) and treats it as '취소'. The X and Esc are '취소' too.
//   ask: false  '자세히 보기' on the GPU page: the same text, how the consent stands, and '닫기'. No checkbox, no agree button.
// OverclockWithdrawDialog below: '동의 철회' asks once, the same way ('취소' first, Enter never withdraws).
using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using SystemBoost.App.Services;

namespace SystemBoost.App.Pages;

public sealed class OverclockConsentDialog : Window
{
    public const int WaitSeconds = 5;
    private const string AgreeText = "동의하고 켜기";
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF7A1A"));

    private readonly CheckBox? _box;
    private readonly Button? _agree;
    private readonly Button _cancel;
    private readonly Stopwatch _shown = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// note: a line under the headline (why it is asked again, or how the consent stands).
    public OverclockConsentDialog(bool ask, string? note = null)
    {
        Title = OverclockNotice.Headline;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SystemBoost/Assets/SystemBoost.ico")));
        Width = 640;
        MinWidth = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        // the buttons sit at the bottom: they must stay on a small or scaled screen, the text scrolls instead
        if (Screens.Primary is { } screen) MaxHeight = Math.Max(360, screen.WorkingArea.Height / screen.Scaling - 60);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24, 20) };
        Add(grid, Head(note), 0);
        var body = new Border
        {
            Child = new ScrollViewer { Content = Body(), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            Padding = new Thickness(0),
        };
        body.Classes.Add("tile");
        Add(grid, body, 1);

        var footer = new StackPanel { Spacing = 12, Margin = new Thickness(0, 14, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        _cancel = Ui.Button(ask ? "취소" : "닫기", "ghost");
        _cancel.IsDefault = true;
        _cancel.IsCancel = true;
        _cancel.Click += (_, _) => Close(false);
        row.Children.Add(_cancel);
        if (ask)
        {
            _box = new CheckBox { Content = new TextBlock { Text = OverclockNotice.Agreement, TextWrapping = TextWrapping.Wrap } };
            _box.IsCheckedChanged += (_, _) => Update();
            footer.Children.Add(_box);
            _agree = Ui.Button(Countdown(WaitSeconds), "primary");
            _agree.IsEnabled = false;
            _agree.Click += (_, _) => { if (Ready) Close(true); };
            row.Children.Add(_agree);
            _timer.Tick += (_, _) => Update();
        }
        footer.Children.Add(row);
        Add(grid, footer, 2);
        Content = grid;

        GuardEnter(this, _agree, () => Close(false));
        Opened += (_, _) =>
        {
            _shown.Start();
            if (ask) _timer.Start();
            Update();
            Dispatcher.UIThread.Post(() => _cancel.Focus(), DispatcherPriority.Input);
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// Enter for the whole window, before any control sees it: never on the risky button (nothing happens there),
    /// anywhere else it is the safe default button.
    internal static void GuardEnter(Window w, Button? risky, Action safe)
    {
        w.AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            if (e.Key != Key.Enter) return;   // Key.Return is the same key
            e.Handled = true;
            if (risky != null && w.FocusManager?.GetFocusedElement() == risky) return;
            safe();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static string Countdown(int left) => left > 0 ? $"{AgreeText} ({left})" : AgreeText;

    private int SecondsLeft => _shown.IsRunning ? Math.Max(0, WaitSeconds - (int)Math.Floor(_shown.Elapsed.TotalSeconds)) : WaitSeconds;

    /// The box is ticked and the wait is over.
    private bool Ready => _box?.IsChecked == true && SecondsLeft == 0;

    private void Update()
    {
        if (_agree == null) return;
        int left = SecondsLeft;
        var text = Countdown(left);
        if (_agree.Content as string != text) _agree.Content = text;
        _agree.IsEnabled = Ready;
        if (left == 0) _timer.Stop();
    }

    private static void Add(Grid grid, Control child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static Control Head(string? note)
    {
        var p = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 14) };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        line.Children.Add(new TextBlock
        {
            Text = OverclockNotice.Icon, FontSize = 22, VerticalAlignment = VerticalAlignment.Center,
            // a colour emoji font where there is one; otherwise the theme font's fallback draws the sign
            FontFamily = new FontFamily("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji, Segoe UI Symbol"),
        });
        var title = Ui.Text(OverclockNotice.Headline, "h1", wrap: true);
        title.FontSize = 22;
        title.FontWeight = FontWeight.Bold;
        title.Foreground = Orange;
        title.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(title);
        p.Children.Add(line);
        if (!string.IsNullOrEmpty(note)) p.Children.Add(Ui.Text(note, "sub", wrap: true));
        return p;
    }

    private static Control Body()
    {
        var p = new StackPanel { Spacing = 14, Margin = new Thickness(18, 16, 26, 18) };   // ScrollViewer padding would push the text past the right edge
        foreach (var paragraph in OverclockNotice.Paragraphs)
        {
            var lines = new StackPanel { Spacing = 4 };
            foreach (var text in paragraph)
            {
                var t = Ui.Text(text, "label", wrap: true);
                t.LineHeight = 21;
                if (text == OverclockNotice.Responsibility) t.FontWeight = FontWeight.Bold;
                lines.Children.Add(t);
            }
            p.Children.Add(lines);
        }
        return p;
    }
}

/// '동의 철회' asks once. ShowDialog<bool> is true only from '철회하기'; '취소' is the default and cancel button with the focus,
/// Enter never withdraws, and the X and Esc are '취소'.
public sealed class OverclockWithdrawDialog : Window
{
    public OverclockWithdrawDialog()
    {
        Title = "동의 철회";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SystemBoost/Assets/SystemBoost.ico")));
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = new StackPanel { Spacing = 12, Margin = new Thickness(24, 20) };
        var title = Ui.Text("오버클럭 · 전력 제한 동의를 철회할까요?", "h2", wrap: true);
        title.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
        p.Children.Add(title);
        p.Children.Add(Ui.Text("오버클럭 / 전력 제한을 끄고, 그래픽카드를 공장 초기값으로 돌려요. "
                               + "다른 프로그램에서 직접 맞춘 전력·클럭 값도 공장 초기값으로 돌아가요.", "label", wrap: true));
        p.Children.Add(Ui.Text("관리자 확인 창이 뜰 수 있어요. 다시 쓰려면 안내를 읽고 다시 동의해야 해요.", "muted", wrap: true));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var cancel = Ui.Button("취소", "ghost");
        cancel.IsDefault = true;
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(false);
        var confirm = Ui.Button("철회하기", "primary");
        confirm.Click += (_, _) => Close(true);
        row.Children.Add(cancel);
        row.Children.Add(confirm);
        p.Children.Add(row);
        Content = p;
        OverclockConsentDialog.GuardEnter(this, confirm, () => Close(false));
        Opened += (_, _) => Dispatcher.UIThread.Post(() => cancel.Focus(), DispatcherPriority.Input);
    }
}
