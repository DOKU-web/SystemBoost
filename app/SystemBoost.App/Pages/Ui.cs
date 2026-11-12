// Small helpers to build pages in code with the theme classes.
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SystemBoost.App.Services;

namespace SystemBoost.App.Pages;

internal static class Ui
{
    public static TextBlock Text(string text, string? cls = null, double? size = null, bool wrap = false)
    {
        var t = new TextBlock { Text = text };
        if (cls != null) foreach (var c in cls.Split(' ')) t.Classes.Add(c);
        if (size is double s) t.FontSize = s;
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    public static Border Card(Control child, string cls = "card")
    {
        var b = new Border { Child = child };
        b.Classes.Add(cls);
        return b;
    }

    public static Button Button(string text, string cls)
    {
        var b = new Button { Content = text };
        b.Classes.Add(cls);
        return b;
    }

    /// Opens a web page in the browser. False when there is no window or the browser did not start.
    public static async Task<bool> OpenUrl(Control from, string url)
    {
        try { return TopLevel.GetTopLevel(from) is { } top && await top.Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception) { return false; }
    }

    /// "개인정보 처리방침 보기": opens /privacy on the seller's server. When the browser does not open,
    /// status (hidden until then) shows the address to type in.
    public static Button PrivacyButton(TextBlock status)
    {
        var b = Button("개인정보 처리방침 보기", "ghost");
        b.Click += async (_, _) =>
        {
            var url = Support.PrivacyUrl;
            if (await OpenUrl(b, url)) { status.IsVisible = false; return; }
            status.Text = $"브라우저를 열지 못했어요. 이 주소를 브라우저에 넣어 주세요: {url}";
            status.IsVisible = true;
        };
        return b;
    }

    public static StackPanel Header(string title, string sub)
    {
        var p = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 20) };
        p.Children.Add(Text(title, "h1"));
        p.Children.Add(Text(sub, "sub", wrap: true));
        return p;
    }

    /// A settings row: title + description on the left, control on the right.
    public static Border Row(string title, string desc, Control right, bool first = false)
    {
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(Text(title, "label"));
        if (!string.IsNullOrEmpty(desc)) left.Children.Add(Text(desc, "muted", wrap: true));
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(left);
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        var b = new Border { Child = g };
        b.Classes.Add("row");
        if (first) b.BorderThickness = new Thickness(0);
        return b;
    }

    public static Border Badge(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFD2B0")), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.Black },
        };
    }

    /// Gray tag shown instead of a control while a feature is not available yet.
    public static Border Pending() => Tag("준비 중");

    /// Gray tag for a quiet state (the orange Badge is for what needs a look).
    public static Border Tag(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#232327")), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#8B8A88")) },
        };
    }
}
