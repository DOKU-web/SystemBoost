using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using SystemBoost.App.Services;
using System;
using System.Linq;

namespace SystemBoost.App.Pages;

// Sits above every page. Its messages carry both languages: whoever uses it may not read the one the app is in.
public sealed class LanguageBar : StackPanel
{
    public LanguageBar()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 10;
        HorizontalAlignment = HorizontalAlignment.Right;

        var box = new ComboBox
        {
            ItemsSource = Lang.Languages.Select(l => l.Name).ToList(),
            MinWidth = 130,
            SelectedIndex = Array.FindIndex(Lang.Languages, l => l.Code == Lang.Code),
        };
        AutomationProperties.SetName(box, "언어 · Language");
        ToolTip.SetTip(box, "언어 · Language");

        var status = Ui.Text("", "muted");
        status.VerticalAlignment = VerticalAlignment.Center;
        var restart = Ui.Button("다시 시작 · Restart", "primary");
        restart.VerticalAlignment = VerticalAlignment.Center;
        restart.IsVisible = false;

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            var code = Lang.Languages[box.SelectedIndex].Code;
            var saved = Lang.Save(code);
            var waiting = saved && code != Lang.Code;
            restart.IsVisible = waiting && Platform.IsWindows;
            status.Text = !saved ? "저장하지 못했어요 · Could not save"
                : !waiting ? ""
                : Platform.IsWindows ? "다시 시작하면 바뀌어요 · Applies after a restart"
                : "앱을 끄고 다시 열면 바뀌어요 · Quit and reopen the app to apply";
        };
        restart.Click += (_, _) =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime d
                || d.MainWindow is not MainWindow w || Environment.ProcessPath is not { } exe) return;
            w.Shutdown(applyUpdateOnExit: false);   // what was changed for a game goes back before the new one starts
            try { System.Diagnostics.Process.Start(exe); }
            catch (Exception) { }   // the choice is saved: the next start has it
            d.Shutdown();
        };

        Children.Add(status);
        Children.Add(restart);
        Children.Add(new TextBlock { Text = "🌐", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(box);
    }
}
