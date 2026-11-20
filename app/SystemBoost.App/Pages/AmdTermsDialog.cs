// Article 12-2 of the licence agreement (AMD 구성 요소, the terms AMD's ADLX licence asks for) in a window, black with the
// orange accent like OverclockConsentDialog. Only builds with the Radeon driver features (AmdBuild.DriverFeatures) open it,
// and only on a PC whose AMD card waits for the consent (GpuCards).
//   ask: true   '동의하고 라데온 기능 쓰기' gives true; it is enabled WaitSeconds after the window opened (the countdown is
//               on the button), and never when the article could not be read. '취소' is the default and cancel button
//               and has the focus; Enter never agrees (OverclockConsentDialog.GuardEnter); the X and Esc are '취소'.
//   ask: false  '조건 보기': the same text, how the consent stands, and '닫기'.
// The article is read from the agreement shipped in the app at run time (AmdTerms.Text: from its "## " heading to the
// next one), the Korean text, or its reference translation when the screen is in English, as the agreement window does.
// AmdTermsWithdrawDialog below: '동의 철회' asks once, the same way ('취소' first, Enter never withdraws).
using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using SystemBoost.App.Services;

namespace SystemBoost.App.Pages;

public sealed class AmdTermsDialog : Window
{
    public const int WaitSeconds = OverclockConsentDialog.WaitSeconds;
    public const string Headline = "AMD 구성 요소 조건 (사용권 계약 제12조의2)";
    private const string AgreeText = "동의하고 라데온 기능 쓰기";
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF7A1A"));

    private readonly Button? _agree;
    private readonly Button _cancel;
    private readonly bool _readable;
    private readonly Stopwatch _shown = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public AmdTermsDialog(bool ask)
    {
        AmdTerms.EnsureDialogAllowed();   // a build without ADLX never opens it (callers check AmdTerms.DialogAllowed)
        Title = "AMD 구성 요소 조건";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SystemBoost/Assets/SystemBoost.ico")));
        Width = 680;
        MinWidth = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        // the buttons sit at the bottom: they must stay on a small or scaled screen, the text scrolls instead
        if (Screens.Primary is { } screen) MaxHeight = Math.Max(360, screen.WorkingArea.Height / screen.Scaling - 60);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var article = AmdTerms.Text();
        _readable = article.Count > 1;

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24, 20) };
        Add(grid, Head(ask ? "라데온 드라이버 기능을 쓰기 전에 아래 조건에 따로 동의해 주세요." : AmdTerms.Standing()), 0);
        var body = new Border
        {
            Child = new ScrollViewer { Content = Body(article), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            Padding = new Thickness(0),
        };
        body.Classes.Add("tile");
        Add(grid, body, 1);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _cancel = Ui.Button(ask ? "취소" : "닫기", "ghost");
        _cancel.IsDefault = true;
        _cancel.IsCancel = true;
        _cancel.Click += (_, _) => Close(false);
        row.Children.Add(_cancel);
        if (ask)
        {
            _agree = Ui.Button(Countdown(WaitSeconds), "primary");
            _agree.IsEnabled = false;
            _agree.Click += (_, _) => { if (Ready) Close(true); };
            row.Children.Add(_agree);
            _timer.Tick += (_, _) => Update();
        }
        Add(grid, row, 2);
        Content = grid;

        OverclockConsentDialog.GuardEnter(this, _agree, () => Close(false));
        Opened += (_, _) =>
        {
            _shown.Start();
            if (ask) _timer.Start();
            Update();
            Dispatcher.UIThread.Post(() => _cancel.Focus(), DispatcherPriority.Input);
        };
        Closed += (_, _) => _timer.Stop();
    }

    private static string Countdown(int left) => left > 0 ? $"{AgreeText} ({left})" : AgreeText;

    private int SecondsLeft => _shown.IsRunning ? Math.Max(0, WaitSeconds - (int)Math.Floor(_shown.Elapsed.TotalSeconds)) : WaitSeconds;

    /// The article is on screen and the wait is over.
    private bool Ready => _readable && SecondsLeft == 0;

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

    private static Control Head(string note)
    {
        var p = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 14) };
        var title = Ui.Text(Headline, "h1", wrap: true);
        title.FontSize = 22;
        title.FontWeight = FontWeight.Bold;
        title.Foreground = Orange;
        p.Children.Add(title);
        p.Children.Add(Ui.Text(note, "sub", wrap: true));
        return p;
    }

    // What the consent does, then the article line by line as the agreement window shows it ("## " heading, "! " bold).
    private static Control Body(System.Collections.Generic.IReadOnlyList<string> article)
    {
        var p = new StackPanel { Spacing = 6, Margin = new Thickness(18, 14, 26, 18) };   // ScrollViewer padding would push the text past the right edge
        var about = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var line in new[]
        {
            "이 조는 사용권 계약에 들어 있고, 2027년 1월 1일부터 모든 사용자에게 적용돼요.",
            "지금 동의하면 그 전에도 이 PC에서 라데온 드라이버 기능(AMD ADLX)을 쓰는 데 이 조가 바로 적용돼요.",
            "동의하지 않으면 라데온 드라이버 기능만 꺼져 있고, 다른 기능은 그대로 쓸 수 있어요. 2027년 1월 1일 전까지는 GPU · 오버클럭 화면에서 동의를 철회할 수 있어요.",
        })
        {
            var t = Ui.Text(line, "label", wrap: true);
            t.LineHeight = 21;
            about.Children.Add(t);
        }
        var untested = Ui.Text(AmdGpuBackend.Untested, "muted", wrap: true);
        untested.Foreground = Orange;
        about.Children.Add(untested);
        p.Children.Add(about);

        if (article.Count <= 1)
        {
            var missing = Ui.Text("사용권 계약의 제12조의2를 읽지 못했어요. 앱을 다시 설치해 주세요.", "label", wrap: true);
            missing.Foreground = Orange;
            p.Children.Add(missing);
            return p;
        }
        // the screen is in English: eula.en.txt, whose first line says the Korean text governs
        if (Lang.Translating) p.Children.Add(Ui.Text("참고 번역이에요. 계약은 한국어 원문이 기준이에요.", "muted", wrap: true));
        foreach (var line in article)
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("## "))
            {
                var heading = Ui.Text(line[3..], "h2 accent", wrap: true);
                heading.Margin = new Thickness(0, 8, 0, 2);
                p.Children.Add(heading);
            }
            else if (line.StartsWith("! "))
            {
                var strong = Ui.Text(line[2..], "label", wrap: true);
                strong.FontWeight = FontWeight.Bold;
                p.Children.Add(strong);
            }
            else p.Children.Add(Ui.Text(line, "label", wrap: true));
        }
        return p;
    }
}

/// '동의 철회' of the consent to Article 12-2 asks once. ShowDialog<bool> is true only from '철회하기'; '취소' is the default
/// and cancel button with the focus, Enter never withdraws, and the X and Esc are '취소'.
public sealed class AmdTermsWithdrawDialog : Window
{
    public AmdTermsWithdrawDialog()
    {
        AmdTerms.EnsureDialogAllowed();   // a build without ADLX never opens it (callers check AmdTerms.DialogAllowed)
        Title = "동의 철회";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SystemBoost/Assets/SystemBoost.ico")));
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = new StackPanel { Spacing = 12, Margin = new Thickness(24, 20) };
        var title = Ui.Text("AMD 구성 요소 조건 동의를 철회할까요?", "h2", wrap: true);
        title.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
        p.Children.Add(title);
        p.Children.Add(Ui.Text("라데온 드라이버 기능을 모두 꺼요. 켜져 있는 오버클럭 / 전력 제한과 드라이버 게임 프로필은 먼저 원래대로 돌리고, "
                               + "돌리지 못하면 철회하지 않아요.", "label", wrap: true));
        p.Children.Add(Ui.Text("관리자 확인 창이 뜰 수 있어요. 2027년 1월 1일부터는 이 조가 모든 사용자에게 적용돼서 따로 동의하지 않아도 "
                               + "라데온 드라이버 기능을 쓸 수 있어요.", "muted", wrap: true));
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
