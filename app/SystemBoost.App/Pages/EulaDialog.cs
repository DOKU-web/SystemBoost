// The licence agreement inside the app, the same on Windows and macOS.
// consent: true  - asked at start; Close(true) needs the box ticked, the X counts as no.
// consent: false - read again from Settings.
// eula.txt is whole lines: "## " heading, "! " bold line. No inline runs, so the text
// stays readable by screen readers.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using SystemBoost.App.Services;

namespace SystemBoost.App.Pages;

public sealed class EulaDialog : Window
{
    private static readonly string[] Important =
    {
        "잠금을 풀거나 프로그램과 키를 재배포하면 안 돼요. 어기면 키가 막히고 사용권이 끝날 수 있어요.",
        "안티치트를 쓰는 게임에서는 시험하지 않았어요. 게임 정책은 쓰기 전에 직접 확인해 주세요.",
        "오버클럭은 게임 강제 종료, 시스템 멈춤, 드라이버 오류를 일으킬 수 있고 보증에 영향을 줄 수 있어요.",
        "키를 등록해 유료 기능을 쓰기 시작하면 청약철회가 제한될 수 있어요.",
        "앱이 켜져 있는 동안 무작위 설치 번호로 사용 통계를 판매자 서버에 보내요. 키, 메일, 게임 이름은 보내지 않고, 설정에서 끌 수 있어요.",
    };

    public EulaDialog(bool consent)
    {
        Title = "SystemBoost 사용권 계약";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SystemBoost/Assets/SystemBoost.ico")));
        Width = 760; Height = 700;
        MinWidth = 520; MinHeight = 440;
        // The buttons sit at the bottom: they must stay on a small or scaled screen.
        if (Screens.Primary is { } screen)
            Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling - 60);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(24, 20),
        };

        var head = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(Ui.Text("사용권 계약", "h1"));
        head.Children.Add(Ui.Text(consent ? "앱을 쓰려면 아래 계약에 동의해야 해요. 동의하지 않으면 앱이 종료돼요." : AcceptedLine(), "sub", wrap: true));
        Add(grid, head, 0);

        // The summary scrolls with the contract: outside it, a small window has no room left for the text.
        var body = new Border { Child = new ScrollViewer { Content = Body(consent), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        body.Classes.Add("tile");
        body.Padding = new Thickness(0);
        Add(grid, body, 1);

        Add(grid, consent ? ConsentFooter() : ReadFooter(), 2);
        Content = grid;
    }

    private static void Add(Grid grid, Control child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static string AcceptedLine() =>
        Eula.AcceptedAt is { } at ? $"{at.LocalDateTime:yyyy년 M월 d일 HH:mm}에 이 계약에 동의했어요." : "이 PC에 동의 기록이 없어요.";

    private static Control Summary()
    {
        var p = new StackPanel { Spacing = 5 };
        p.Children.Add(Ui.Text("꼭 확인할 내용", "h2 accent"));
        foreach (var line in Important) p.Children.Add(Ui.Text("· " + line, "label", wrap: true));
        var card = Ui.Card(p, "note");
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private static Control Body(bool consent)
    {
        var p = new StackPanel { Spacing = 6, Margin = new Thickness(18, 14, 26, 18) };   // ScrollViewer padding would push the text past the right edge
        if (consent) p.Children.Add(Summary());
        foreach (var line in Eula.Lines())
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("## "))
            {
                var heading = Ui.Text(line[3..], "h2 accent", wrap: true);
                heading.Margin = new Thickness(0, 12, 0, 2);
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

    private Control ConsentFooter()
    {
        var p = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        var agree = new CheckBox { Content = "계약 내용을 읽었고 이 계약에 동의합니다" };
        p.Children.Add(agree);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var refuse = Ui.Button("동의하지 않고 끝내기", "ghost");
        refuse.Click += (_, _) => Close(false);
        var accept = Ui.Button("동의하고 시작", "primary");
        accept.IsEnabled = false;
        agree.IsCheckedChanged += (_, _) => accept.IsEnabled = agree.IsChecked == true;
        accept.Click += (_, _) => Close(true);
        row.Children.Add(refuse);
        row.Children.Add(accept);
        p.Children.Add(row);
        return p;
    }

    private Control ReadFooter()
    {
        var close = Ui.Button("닫기", "ghost");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 12, 0, 0);
        close.Click += (_, _) => Close();
        return close;
    }
}
