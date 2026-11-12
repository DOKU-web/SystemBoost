// Popup shown when a purchase completes: the license key (already registered) + copy.
// A bundle shows every key: the first is registered on this PC, the others go to the buyer's other PCs.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using SystemBoost.App.Services;

namespace SystemBoost.App.Pages;

public sealed class KeyDialog : Window
{
    public KeyDialog(string key, string? note, string[]? keys = null)
    {
        keys = keys is { Length: > 0 } ? keys : new[] { key };
        bool bundle = keys.Length > 1;
        Title = "SystemBoost " + PlanStatus.Name;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SystemBoost/Assets/SystemBoost.ico")));
        Width = bundle ? 640 : 520; SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        var title = Ui.Text("결제가 확인됐어요 ★", "h1");
        title.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
        p.Children.Add(title);
        var on = PlanStatus.Name == "산업" ? "산업이" : PlanStatus.Name + "가";
        p.Children.Add(Ui.Text(note ?? (bundle
            ? $"{on} 이 PC에 켜졌어요(첫 번째 키). 키 {keys.Length}개를 받았어요. 나머지 {keys.Length - 1}개는 다른 PC에 하나씩 등록하세요. 키 하나는 PC 한 대에만 써요."
            : $"{on} 켜졌어요. 아래 키는 다른 PC에 등록하거나 다시 설치할 때 필요하니 따로 보관하세요."), "sub", wrap: true));
        var all = string.Join(Environment.NewLine, keys);
        var box = new TextBox { Text = all, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = bundle ? 260 : 96, FontFamily = new FontFamily("Consolas, Menlo, monospace") };
        p.Children.Add(box);
        if (bundle) p.Children.Add(Ui.Text("한 줄에 키 하나예요. 이 창을 닫기 전에 복사하거나 파일로 저장해 두세요. 키를 잃어버리면 주문 코드와 함께 판매자에게 문의하세요.", "muted", wrap: true));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var copy = Ui.Button(bundle ? "모든 키 복사" : "키 복사", "primary");
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null) await Clipboard.SetTextAsync(all);
            copy.Content = "복사했어요";
        };
        bar.Children.Add(copy);
        if (bundle)
        {
            var save = Ui.Button("파일로 저장", "ghost");
            save.Click += async (_, _) =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Services.Lang.T("라이선스 키 저장"), SuggestedFileName = Services.Lang.T($"SystemBoost 키 {keys.Length}개.txt"), DefaultExtension = "txt",
                    FileTypeChoices = new[] { new FilePickerFileType(Services.Lang.T("텍스트")) { Patterns = new[] { "*.txt" } } },
                });
                if (file == null) return;
                await using var s = await file.OpenWriteAsync();
                await using var w = new System.IO.StreamWriter(s, new System.Text.UTF8Encoding(false));
                foreach (var k in keys) await w.WriteLineAsync(k);   // one key a line, ready to paste
                save.Content = "저장했어요";
            };
            bar.Children.Add(save);
        }
        var close = Ui.Button("닫기", "ghost");
        close.Click += (_, _) => Close();
        bar.Children.Add(close);
        p.Children.Add(bar);
        p.Children.Add(PurchaseInfo.Receipt(key, keys.Length));   // the purchase confirmation to keep (PurchaseInfo.cs)
        Content = p;
    }
}
