// Plan window: what each plan adds, the plan in use, and the way up. Paying stays on the Premium page.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using SystemBoost.App.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemBoost.App.Pages;

public sealed class PlanDialog : Window
{
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF7A1A"));

    private readonly (string feature, string free, string premium, string ultra, bool windowsOnly)[] _rows;
    private readonly Grid _cards = new() { ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*") };
    private string _period = "yearly";

    /// What the buyer picked: tier and period for the Premium page. Null when closed without picking.
    public (bool Ultra, string Period)? Choice { get; private set; }

    public PlanDialog(bool boostReady)
    {
        _rows = PremiumPage.Rows.Where(r => boostReady || r.feature != PremiumPage.BoostRow).ToArray();
        Title = "플랜 업그레이드";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SystemBoost/Assets/SystemBoost.ico")));
        Width = 960; SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var p = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        var title = Ui.Text("플랜 업그레이드", "h1");
        title.Foreground = Orange;
        p.Children.Add(title);
        p.Children.Add(Ui.Text("플랜마다 쓸 수 있는 기능을 비교하고 더 높은 플랜으로 올려요.", "muted", wrap: true));

        var periods = new StackPanel { Orientation = Orientation.Horizontal };
        var periodBox = new Border { Child = periods, HorizontalAlignment = HorizontalAlignment.Left };
        periodBox.Classes.Add("segbox");
        foreach (var (period, text) in new[] { ("monthly", "월간"), ("yearly", "연간") })
        {
            var t = new ToggleButton { Content = text, IsChecked = period == _period };
            t.Classes.Add("seg");
            t.Click += (_, _) =>
            {
                _period = period;
                foreach (var o in periods.Children.OfType<ToggleButton>()) o.IsChecked = o == t;
                ShowCards();
            };
            periods.Children.Add(t);
        }
        p.Children.Add(periodBox);
        p.Children.Add(_cards);
        ShowCards();

        if (PlanStatus.Rank > 0)
            p.Children.Add(Ui.Text("업그레이드 키를 등록하면 지금 키를 대신해요. 지금 키의 남은 기간은 이어지지 않아요.", "muted", wrap: true));
        var close = Ui.Button("닫기", "ghost");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        p.Children.Add(close);
        Content = new ScrollViewer { Content = p, MaxHeight = 860 };
    }

    private void ShowCards()
    {
        bool monthly = _period == "monthly";
        _cards.Children.Clear();
        Add(0, Card(0, "무료", "₩0", Lines(null, r => r.free, _ => "—")));
        Add(2, Card(1, "프리미엄", monthly ? PremiumPage.PriceMonthly : PremiumPage.PriceYearly,
                    Lines("무료의 모든 기능", r => r.premium, r => r.free)));
        Add(4, Card(2, "울트라", monthly ? PremiumPage.PriceUltraMonthly : PremiumPage.PriceUltraYearly,
                    Lines("프리미엄의 모든 기능", r => r.ultra, r => r.premium)));
    }

    private void Add(int column, Control card)
    {
        Grid.SetColumn(card, column);
        _cards.Children.Add(card);
    }

    private List<Control> Lines(string? first,
        Func<(string feature, string free, string premium, string ultra, bool windowsOnly), string> cell,
        Func<(string feature, string free, string premium, string ultra, bool windowsOnly), string> below)
    {
        var lines = new List<Control>();
        if (first != null) lines.Add(Ui.Text(first, "label", wrap: true));
        foreach (var r in _rows)
        {
            var value = cell(r);
            if (value == "—" || value == below(r)) continue;
            lines.Add(Ui.Text(r.feature, "muted", wrap: true));
            if (value != "●")
            {
                var v = Ui.Text(value, "label", wrap: true);
                v.Foreground = Orange;
                lines.Add(v);
            }
        }
        return lines;
    }

    private Control Card(int rank, string name, string price, List<Control> lines)
    {
        bool current = rank == PlanStatus.Rank;
        var p = new StackPanel { Spacing = 8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text(name, "h2"));
        if (current) head.Children.Add(Ui.Badge("사용 중"));
        p.Children.Add(head);
        var cost = Ui.Text(price, "big");
        cost.Foreground = Orange;
        p.Children.Add(cost);
        if (current && rank > 0) p.Children.Add(Ui.Text(PlanStatus.Period, "muted", wrap: true));
        p.Children.Add(Pick(rank));
        foreach (var line in lines) p.Children.Add(line);
        var card = Ui.Card(p);
        card.VerticalAlignment = VerticalAlignment.Stretch;
        if (current) { card.BorderBrush = Orange; card.BorderThickness = new Thickness(1); }
        return card;
    }

    private Button Pick(int rank)
    {
        var now = PlanStatus.Rank;
        bool up = rank > now;
        bool extend = rank == now && PlanStatus.CanExtend;
        var text = up ? (rank == 2 ? "울트라로 업그레이드" : "프리미엄으로 업그레이드")
                 : extend ? "기간 연장"
                 : rank == now ? "사용 중"
                 : "포함되어 있어요";
        var b = Ui.Button(text, up ? "primary" : "ghost");
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        b.IsEnabled = up || extend;
        b.Click += (_, _) => { Choice = (rank == 2, _period); Close(); };
        return b;
    }
}
