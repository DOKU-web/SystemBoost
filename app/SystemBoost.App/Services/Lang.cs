// Screen language. The code keeps its Korean text, so logic that reads a status or a plan name is
// unaffected: the text is replaced where it reaches a control. Assets/lang.<code>.json maps each
// Korean string to its translation; {0}, {1} stand for what the code fills in. A new language is a
// new file plus a line in Languages. Text with no entry stays Korean; game titles are left alone that way.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SystemBoost.App.Services;

public static class Lang
{
    public const string Korean = "ko";
    public static readonly (string Code, string Name)[] Languages = { (Korean, "한국어"), ("en", "English") };

    private static string File_ => Path.Combine(Platform.DataDir, "language.txt");
    private static Dictionary<string, string> _exact = new();
    private static List<(Regex Pattern, string Text)> _templates = new();
    private static readonly Regex Slot = new(@"\{(\d+)\}", RegexOptions.Compiled);

    public static string Code { get; private set; } = Korean;
    public static bool Translating => Code != Korean;

    /// The saved choice, otherwise the operating system's language.
    public static string Saved()
    {
        try
        {
            var s = File.Exists(File_) ? File.ReadAllText(File_).Trim() : "";
            if (Languages.Any(l => l.Code == s)) return s;
        }
        catch (Exception) { }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Korean ? Korean : "en";
    }

    public static bool Save(string code)
    {
        try { File.WriteAllText(File_, code); return true; }
        catch (Exception) { return false; }
    }

    public static void Start()
    {
        Code = Saved();
        if (!Translating) return;
        Load(Code);
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((t, e) => Apply(t, TextBlock.TextProperty, e.NewValue as string));
        Run.TextProperty.Changed.AddClassHandler<Run>((r, e) => Apply(r, Run.TextProperty, e.NewValue as string));
        TextBox.WatermarkProperty.Changed.AddClassHandler<TextBox>((t, e) => Apply(t, TextBox.WatermarkProperty, e.NewValue as string));
        AutomationProperties.NameProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, AutomationProperties.NameProperty, e.NewValue as string));
        Window.TitleProperty.Changed.AddClassHandler<Window>((w, e) => Apply(w, Window.TitleProperty, e.NewValue as string));
    }

    private static void Apply<T>(T target, Avalonia.StyledProperty<string?> property, string? text) where T : Avalonia.AvaloniaObject
    {
        if (text == null) return;
        var done = T_(text);
        if (done != text) target.SetCurrentValue(property, done);
    }

    private static void Load(string code)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://SystemBoost/Assets/lang.{code}.json"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var all = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? new();
        foreach (var (key, value) in all.OrderByDescending(p => p.Key.Length))
        {
            if (!Slot.IsMatch(key)) { _exact[key] = value; continue; }
            var pattern = "^" + Slot.Replace(Regex.Escape(key).Replace(@"\{", "{"), "(?<s$1>.+?)") + "$";
            _templates.Add((new Regex(pattern, RegexOptions.Compiled | RegexOptions.Singleline), value));
        }
    }

    private static bool HasKorean(string s)
    {
        foreach (var c in s) if (c is >= '가' and <= '힣') return true;
        return false;
    }

    /// The text in the chosen language. Also for text drawn by hand, which no control carries.
    public static string T(string text) => Translating ? T_(text) : text;

    private static string T_(string text)
    {
        if (!HasKorean(text)) return text;
        if (text.Contains('\n')) return string.Join("\n", text.Split('\n').Select(T_));
        var trimmed = text.Trim();
        if (_exact.TryGetValue(text, out var hit) || _exact.TryGetValue(trimmed, out hit)) return hit;
        foreach (var (pattern, value) in _templates)
        {
            var m = pattern.Match(trimmed);
            if (m.Success) return Slot.Replace(value, s => T_(m.Groups["s" + s.Groups[1].Value].Value));
        }
        // joined at run time: a list dot, parts of a line, then sentences of a paragraph
        if (trimmed.StartsWith("· ")) return "· " + T_(trimmed[2..]);
        var parts = trimmed.Split(" · ");
        if (parts.Length > 1) return string.Join(" · ", parts.Select(T_));
        var sentences = Regex.Split(trimmed, @"(?<=[요다]\.)\s+");
        if (sentences.Length == 1) return text;
        var done = new List<string>();
        for (int i = 0, end; i < sentences.Length; i = end)
        {
            // an entry can hold several of the sentences
            for (end = sentences.Length; end > i + 1 && !_exact.ContainsKey(string.Join(" ", sentences[i..end])); end--) { }
            done.Add(T_(string.Join(" ", sentences[i..end])));
        }
        return string.Join(" ", done);
    }
}
