// Popup shown when a purchase completes: the license key (already registered) + copy.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace SystemBoost.App.Pages;

public sealed class KeyDialog : Window
{
    public KeyDialog(string key, string? note)
    {
        Title = "SystemBoost 프리미엄";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SystemBoost/Assets/SystemBoost.ico")));
        Width = 520; SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        var title = Ui.Text("결제가 확인됐어요 ★", "h1");
        title.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
        p.Children.Add(title);
        p.Children.Add(Ui.Text(note ?? "프리미엄이 켜졌어요. 아래 키는 다른 PC에 등록하거나 다시 설치할 때 필요하니 따로 보관하세요.", "sub", wrap: true));
        var box = new TextBox { Text = key, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 96, FontFamily = new FontFamily("Consolas, Menlo, monospace") };
        p.Children.Add(box);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var copy = Ui.Button("키 복사", "primary");
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null) await Clipboard.SetTextAsync(key);
            copy.Content = "복사했어요";
        };
        var close = Ui.Button("닫기", "ghost");
        close.Click += (_, _) => Close();
        bar.Children.Add(copy); bar.Children.Add(close);
        p.Children.Add(bar);
        Content = p;
    }
}
