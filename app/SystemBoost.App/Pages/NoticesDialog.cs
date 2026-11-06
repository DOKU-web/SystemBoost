// Shows THIRD_PARTY_NOTICES.txt inside the app, the same on Windows and macOS.
using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Platform;

namespace SystemBoost.App.Pages;

public sealed class NoticesDialog : Window
{
    public NoticesDialog(string path)
    {
        Title = "오픈소스 라이선스 전문";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SystemBoost/Assets/SystemBoost.ico")));
        Width = 780; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // About 5,400 lines: only the lines on screen are laid out, a text box would stall.
        var lines = File.ReadAllLines(path).Select(l => l.Length == 0 ? " " : l).ToList();
        var list = new ItemsControl
        {
            ItemsSource = lines,
            Margin = new Thickness(24, 20, 30, 20),   // ScrollViewer padding would push the text past the right edge
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap }),
        };
        var scroll = new ScrollViewer { Content = list };
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = scroll;
    }
}
