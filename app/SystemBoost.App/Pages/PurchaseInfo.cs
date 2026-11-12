// What a buyer sees before paying and can keep afterwards: the seller's details, the licence agreement, the
// withdrawal terms as 사용권 계약 제9조 states them, and the minors notice (제3조 제11항). Same on Windows and macOS.
// The seller's details are the dashboard's 고객센터 settings (config:support, GET /support/info), the same as
// 고객센터 → 판매자 정보 and /help. Only fields with a value are shown: an empty field gets no line, so nothing hints
// at a registration number that does not exist.
//   BeforePaying()  ★ 플랜, in the buy card above the pay button
//   Receipt(...)    KeyDialog, once a key arrived: a purchase confirmation to copy or save
//   Seller(...)     the seller's details as rows (고객센터 → 판매자 정보 uses it too)
// Every Korean line here has an entry in Assets/lang.en.json; the copied or saved text goes through Lang.T.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SystemBoost.App.Services;

namespace SystemBoost.App.Pages;

internal static class PurchaseInfo
{
    /// 사용권 계약 제9조 제1항. The dashboard may announce a longer period, never a shorter one.
    public const int LegalDays = 7;
    public static int WithdrawDays(SupportInfo info) => Math.Max(LegalDays, info.RefundDays);

    private const string NotEntered = "판매자가 아직 입력하지 않았어요";
    private const string NotLoaded = "판매자 정보를 불러오지 못했어요. 인터넷에 연결되면 다시 보여요.";
    public const string MinorsNotice = "미성년자가 법정대리인의 동의 없이 결제하면 미성년자 본인이나 법정대리인이 그 계약을 취소할 수 있어요(사용권 계약 제3조 제11항).";
    private const string Limited = "결제가 확인되면 앱이 키를 이 PC에 바로 등록해요. 등록한 키로 유료 기능을 쓰기 시작하면 청약철회가 제한될 수 있어요(사용권 계약 제9조 제2항).";
    private const string NotAsShown = "표시·광고와 다르거나 계약과 다르게 제공되면 받은 날부터 3개월, 그 사실을 안 날부터 30일 안에 철회할 수 있어요(제9조 제3항).";
    private const string HowToAsk = "환불은 앱의 고객센터 → '환불 요청'이나 판매자 서버의 도움말 페이지(/help)에서 요청해요.";

    // ------------------------------------------------------------ the seller
    /// Only the fields the seller filled in, labelled as on 고객센터 and /help.
    public static List<(string Label, string Value)> SellerRows(SupportSeller s)
    {
        var rows = new List<(string, string)>();
        void Add(string label, string value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value.Trim())); }
        Add("상호", s.Name); Add("대표자", s.Rep); Add("주소", s.Address); Add("사업자등록번호", s.BizNo);
        Add("통신판매업 신고번호", s.MailOrderNo); Add("메일", s.Email); Add("전화", s.Phone); Add("문의 받는 시간", s.Hours);
        return rows;
    }

    /// The filled-in fields as rows; with none, one line that says why.
    public static Control Seller(SupportSeller s, bool reachable)
    {
        var rows = SellerRows(s);
        if (rows.Count == 0) return Ui.Text(reachable ? NotEntered : NotLoaded, "muted", wrap: true);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows.Count))) };
        for (int i = 0; i < rows.Count; i++)
        {
            var left = Ui.Text(rows[i].Label, "muted");
            var right = new SelectableTextBlock { Text = rows[i].Value, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            right.Classes.Add("label");
            Grid.SetRow(left, i);
            Grid.SetRow(right, i);
            Grid.SetColumn(right, 1);
            g.Children.Add(left);
            g.Children.Add(right);
        }
        return g;
    }

    // ------------------------------------------------------------ before paying (★ 플랜)
    /// The seller, the agreement and the withdrawal terms, filled in once the seller's details have loaded.
    public static Control BeforePaying()
    {
        var p = new StackPanel { Spacing = 6 };
        var head = Ui.Text("결제 전에 확인해 주세요", "label");
        head.FontWeight = Avalonia.Media.FontWeight.Bold;
        p.Children.Add(head);
        p.Children.Add(Ui.Text("판매자", "muted"));
        var seller = new ContentControl { Content = Ui.Text("불러오는 중…", "muted") };
        p.Children.Add(seller);
        var days = Ui.Text(DaysLine(LegalDays), "label", wrap: true);
        p.Children.Add(days);
        foreach (var line in new[] { Limited, NotAsShown, HowToAsk, MinorsNotice }) p.Children.Add(Ui.Text(line, "muted", wrap: true));
        p.Children.Add(Links());
        p.AttachedToVisualTree += async (_, _) =>
        {
            var info = await Support.InfoAsync();
            seller.Content = Seller(info.Seller, info.Reachable);
            days.Text = DaysLine(WithdrawDays(info));
        };
        var card = Ui.Card(p, "note");
        card.Margin = new Thickness(0, 4, 0, 0);
        return card;
    }

    private static string DaysLine(int days) => $"청약철회: 키를 받은 날부터 {days}일 안에 할 수 있어요(사용권 계약 제9조).";

    /// "사용권 계약 보기" and "개인정보 처리방침 보기".
    private static Control Links()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var eula = Ui.Button("사용권 계약 보기", "ghost");
        eula.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(eula) is Window owner) await new EulaDialog(consent: false).ShowDialog(owner);
        };
        row.Children.Add(eula);
        var status = Ui.Text("", "muted", wrap: true);
        status.IsVisible = false;
        row.Children.Add(Ui.PrivacyButton(status));
        var p = new StackPanel { Spacing = 6 };
        p.Children.Add(row);
        p.Children.Add(status);
        return p;
    }

    // ------------------------------------------------------------ after the key arrived (KeyDialog)
    /// A purchase confirmation the buyer can copy or save: plan, dates, how it is paid, the agreement's version,
    /// the withdrawal terms and the seller. keys = how many keys the order gave (a bundle has one per PC).
    public static Control Receipt(string key, int keys)
    {
        var typed = License.Inspect(key, out _);
        var p = new StackPanel { Spacing = 6 };
        p.Children.Add(Ui.Text("구매 확인서 (보관용)", "h2"));
        p.Children.Add(Ui.Text("계약 내용이에요. 복사하거나 파일로 저장해 두세요.", "muted", wrap: true));
        var body = new StackPanel { Spacing = 4 };
        foreach (var line in Lines(typed, keys, Support.Fallback)) body.Children.Add(Ui.Text(line, "label", wrap: true));
        p.Children.Add(body);
        var seller = new ContentControl();
        p.Children.Add(Ui.Text("판매자", "muted"));
        p.Children.Add(seller);

        var info = Support.Fallback;
        p.AttachedToVisualTree += async (_, _) =>
        {
            info = await Support.InfoAsync();
            body.Children.Clear();
            foreach (var line in Lines(typed, keys, info)) body.Children.Add(Ui.Text(line, "label", wrap: true));
            seller.Content = Seller(info.Seller, info.Reachable);
        };
        seller.Content = Ui.Text("불러오는 중…", "muted");

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var copy = Ui.Button("확인서 복사", "ghost");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } board) await board.SetTextAsync(Text(typed, keys, info));
            copy.Content = "복사했어요";
        };
        bar.Children.Add(copy);
        var save = Ui.Button("확인서 저장", "ghost");
        save.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(save)?.StorageProvider is not { } storage) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Lang.T("구매 확인서 저장"), SuggestedFileName = Lang.T("SystemBoost 구매 확인서.txt"), DefaultExtension = "txt",
                FileTypeChoices = new[] { new FilePickerFileType(Lang.T("텍스트")) { Patterns = new[] { "*.txt" } } },
            });
            if (file == null) return;
            await using var s = await file.OpenWriteAsync();
            await using var w = new System.IO.StreamWriter(s, new System.Text.UTF8Encoding(false));
            await w.WriteAsync(Text(typed, keys, info));
            save.Content = "저장했어요";
        };
        bar.Children.Add(save);
        var eula = Ui.Button("사용권 계약 보기", "ghost");
        eula.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(eula) is Window owner) await new EulaDialog(consent: false).ShowDialog(owner);
        };
        bar.Children.Add(eula);
        p.Children.Add(bar);
        // scrolls by itself: the key window has no scroll bar and grows with its content (a bundle's key box is taller)
        return new ScrollViewer { Content = Ui.Card(p, "note"), MaxHeight = keys > 1 ? 240 : 360, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private static string PlanName(string plan) => plan switch { "biz" => "산업", "ultra" => "울트라", _ => "프로" };

    /// The confirmation's lines in Korean (controls translate themselves; Text() translates for the clipboard).
    private static List<string> Lines(TypedKey? typed, int keys, SupportInfo info)
    {
        var lines = new List<string>();
        var k = typed?.Info;
        if (k != null)
        {
            lines.Add(keys > 1 ? $"플랜: {PlanName(k.Plan)} · 키 {keys}개 (PC 한 대에 키 하나)" : $"플랜: {PlanName(k.Plan)}");
            if (k.Issued is DateTime issued)
            {
                lines.Add($"키를 받은 날: {issued.ToLocalTime():yyyy-MM-dd}");
                lines.Add($"청약철회: {issued.ToLocalTime().AddDays(WithdrawDays(info)):yyyy-MM-dd}까지 (키를 받은 날부터 {WithdrawDays(info)}일)");
            }
            else lines.Add($"청약철회: 키를 받은 날부터 {WithdrawDays(info)}일 안에");
            lines.Add(k.Expires is DateTime end ? $"사용 기간: {end.ToLocalTime():yyyy-MM-dd}까지" : "사용 기간: 기간 제한 없음");
            lines.Add(k.Sub != null ? "결제 방식: 카드 구독 · 해지할 때까지 같은 기간마다 자동으로 결제돼요" : "결제 방식: 직접 결제 · 자동으로 결제되지 않아요");
            if (keys == 1) lines.Add($"키 식별 번호: {typed!.Id}");
        }
        else lines.Add($"청약철회: 키를 받은 날부터 {WithdrawDays(info)}일 안에");
        lines.Add(Limited);
        lines.Add(NotAsShown);
        lines.Add(HowToAsk);
        lines.Add(MinorsNotice);
        lines.Add($"사용권 계약: {Eula.Version} 판 (앱의 '설정' → '사용권 계약 보기')");
        lines.Add($"고객센터: {License.ServerUrl}/help");
        return lines;
    }

    private static string Text(TypedKey? typed, int keys, SupportInfo info)
    {
        var all = new List<string> { Lang.T("SystemBoost 구매 확인서"), "" };
        all.AddRange(Lines(typed, keys, info).Select(Lang.T));
        all.Add("");
        all.Add(Lang.T("판매자"));
        var rows = SellerRows(info.Seller);
        if (rows.Count == 0) all.Add(Lang.T(info.Reachable ? NotEntered : NotLoaded));
        foreach (var (label, value) in rows) all.Add($"{Lang.T(label)}: {value}");
        return string.Join(Environment.NewLine, all) + Environment.NewLine;
    }
}
