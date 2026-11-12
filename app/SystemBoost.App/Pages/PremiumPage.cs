// Premium subscription window: plan comparison, monthly / yearly, bundles for several PCs, buy, enter key.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SystemBoost.App.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SystemBoost.App.Pages;

public sealed class PremiumPage : UserControl
{
    // Prices shown in the app. TODO: set real prices (must match the shop / Steam).
    public const string PriceMonthly = "₩2,900 / 월";
    public const string PriceYearly = "₩19,900 / 년";
    public const string PriceUltraMonthly = "₩8,900 / 월";
    public const string PriceUltraYearly = "₩59,900 / 년";
    public const string PriceBizMonthly = "₩12,900 / 월";
    public const string PriceBizYearly = "₩89,900 / 년";
    public const string PriceSteamDlc = "₩14,000";
    // Yearly bundles, one key per PC: the price per PC is the yearly price 15% off (5 PCs) or 25% off (10 PCs),
    // rounded down to 100 won, so the discount is at least that.
    // Must match the payment links in the dashboard.
    private static readonly Dictionary<(string tier, int seats), (int total, int each)> Bundles = new()
    {
        [("premium", 5)] = (84_500, 16_900), [("premium", 10)] = (149_000, 14_900),
        [("ultra", 5)] = (254_500, 50_900), [("ultra", 10)] = (449_000, 44_900),
        [("biz", 5)] = (382_000, 76_400), [("biz", 10)] = (674_000, 67_400),
    };
    private static readonly int[] SeatChoices = { 1, 5, 10 };

    private readonly TextBlock _state = Ui.Text("", "h2");
    private readonly TextBlock _stateSub = Ui.Text("", "sub", wrap: true);
    private readonly TextBlock _price = Ui.Text(PriceMonthly, "big");
    private readonly TextBox _key = new() { Watermark = "받은 라이선스 키를 붙여 넣으세요", MinWidth = 360 };
    // this PC's key: hidden until '보기', so a screenshot or a screen share does not show it
    private readonly StackPanel _currentPanel = new() { Spacing = 6, IsVisible = false };
    private readonly TextBlock _currentInfo = Ui.Text("", "muted", wrap: true);
    private readonly TextBox _current = new() { IsReadOnly = true, PasswordChar = '•', MinWidth = 360 };
    private readonly Button _currentShow = Ui.Button("보기", "ghost");
    private readonly TextBox _claim = new() { Watermark = "SB-XXXX-XXXX", MaxLength = 40, MinWidth = 200 };
    private readonly TextBlock _claimMsg = Ui.Text("", "muted", wrap: true);
    private readonly StackPanel _otherOrders = new() { Spacing = 6, IsVisible = false };
    private string? _shown;   // the order the order panel shows; null = the newest
    private readonly Button _cancelBtn = Ui.Button("주문 취소", "ghost");
    // Two clicks for what cannot be undone from here: the first click says what will happen, the second does it.
    private readonly Dictionary<Button, object?> _armed = new();
    private bool Confirmed(Button b, string again)
    {
        if (_armed.Remove(b, out var original)) { b.Content = original; return true; }
        _armed[b] = b.Content;
        b.Content = again;
        return false;
    }
    private void Disarm(Button b) { if (_armed.Remove(b, out var original)) b.Content = original; }
    private readonly TextBlock _msg = Ui.Text("", "muted", wrap: true);
    private readonly TextBlock _buyMsg = Ui.Text("", "muted", wrap: true);
    private readonly CheckBox _ticketBox = new() { IsVisible = false };
    private int _ticketPercent;
    private string _plan = "yearly";
    private string _tier = "premium";   // premium, ultra, pro
    private int _seats = 1;             // 5 or 10 = a yearly bundle
    private string PlanId => IdOf(_tier, _plan, _seats);
    private static string IdOf(string tier, string period, int seats) =>
        (tier == "premium" ? "" : tier + "_") + period + (seats > 1 ? "_x" + seats : "");
    private readonly Button _buyBtn = Ui.Button("결제 준비 중", "primary");
    private System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> _methods = new() { ["card"] = new(), ["manual"] = new() };
    private string _method = "card";
    private readonly StackPanel _methodP = new() { Orientation = Orientation.Horizontal };
    private readonly Border _methodBox = new() { HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private readonly TextBlock _methodNote = Ui.Text("", "muted", wrap: true);
    private readonly StackPanel _paidPanel = new() { Spacing = 8 };
    private readonly TextBox _payer = new() { Watermark = "입금자명 / 결제자 이름", MinWidth = 200, MaxLength = 40 };
    private readonly TextBox _memo = new() { Watermark = "메모 (선택: 결제 수단, 시각 등)", MinWidth = 240, MaxLength = 100 };
    private readonly StackPanel _orderPanel = new() { Spacing = 8, IsVisible = false };
    private readonly TextBlock _orderCode = Ui.Text("", "big");
    private readonly TextBlock _orderSub = Ui.Text("", "muted", wrap: true);
    private readonly TextBlock _keyNote = Ui.Text("", "muted", wrap: true);
    private readonly Border _buyCard;   // shown only while something is on sale
    private readonly Border _keyCard;
    private readonly StackPanel _tierP = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _periodP = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _seatsP = new() { Orientation = Orientation.Horizontal };
    private readonly Border _seatsBox = new() { HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private readonly TextBlock _bundleNote = Ui.Text("", "muted", wrap: true);

    // Must match License.UltraOnly and License.BizOnly. windowsOnly: macOS does not allow these, see MacBoostBackend.
    // Biz adds only Windows features, so a Mac shows no Biz column and sells no Biz.
    internal sealed record PlanRow(string Feature, string Free, string Premium, string Ultra, string Biz, bool WindowsOnly);
    private static readonly PlanRow[] AllRows =
    {
        new("프레임 생성 배수", "2배", "자동 · 2 · 3 · 4배", "자동 · 2 · 3 · 4 · 6 · 8배", "자동 · 2 · 3 · 4 · 6 · 8배", false),
        new("품질 모드", "저사양 · 성능 · 균형", "저사양 · 성능 · 균형 · 품질", "저사양 · 성능 · 균형 · 품질", "저사양 · 성능 · 균형 · 품질", false),
        new("업스케일링 + 선명화", "●", "●", "●", "●", false),
        new("게임 부스트", "●", "●", "●", "●", false),
        new("모니터링 · 벤치마크", "●", "●", "●", "●", false),
        new("자원 분배 (사용량 그래프 · 우선/낮춤)", "●", "●", "●", "●", false),
        new("자원 분배 · CPU 최대 사용량 제한", "—", "●", "●", "●", true),
        new("듀얼 GPU (내장 그래픽에 생성 맡기기)", "—", "—", "●", "●", false),
        new("게임별 자동 최적화 (측정해서 효과 있는 것만)", "—", "—", "●", "●", true),
        new("GPU 오버클럭 / 전력 제한 (Windows, NVIDIA)", "—", "—", "●", "●", true),
        new("방송 모드 (방송 프로그램 우선 · 방송 중 알림 끄기)", "—", "—", "—", "●", true),
        new("업무 모드 (화상 회의 · 업무 프로그램 우선)", "—", "—", "—", "●", true),
        new("시작 프로그램 정리", "—", "—", "—", "●", true),
    };
    internal static readonly PlanRow[] Rows = AllRows.Where(r => Platform.IsWindows || !r.WindowsOnly).ToArray();
    internal static bool SellsBiz => Platform.IsWindows;
    internal const string BoostRow = "게임 부스트";
    private readonly bool _boostReady;

    private Control ReferralCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("추천 코드", "h2"));
        p.Children.Add(Ui.Text("추천 코드가 있으면 적어 주세요. 코드에 정해진 플랜을 그 기간 동안 쓸 수 있어요. 기기 한 대에 한 번만 쓸 수 있고, 코드와 무작위 기기 번호를 판매자 서버로 보내요.", "muted", wrap: true));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var box = new TextBox { Watermark = "추천 코드" };   // no length cap: a key pasted here is told where it goes, not cut off
        var apply = Ui.Button("적용", "primary");
        apply.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(apply, 1);
        var msg = Ui.Text("", "muted", wrap: true);
        apply.Click += async (_, _) =>
        {
            if ((box.Text ?? "").Contains('.')) { msg.Text = "라이선스 키는 위 '라이선스 키' 칸에 넣어 주세요."; return; }
            if (License.NormalizeOrderCode(box.Text) != null) { msg.Text = "주문 코드는 위 '주문 코드로 키 받기' 칸에 넣어 주세요."; return; }
            apply.IsEnabled = false;
            var err = await License.RedeemReferralAsync(box.Text ?? "");
            apply.IsEnabled = true;
            msg.Text = err ?? PlanStatus.Rank switch
            {
                3 => "산업이 켜졌어요. 남은 기간은 위에 표시돼요.",
                2 => "울트라가 켜졌어요. 남은 기간은 위에 표시돼요.",
                _ => "프로가 켜졌어요. 남은 기간은 위에 표시돼요.",
            };
            if (err == null) box.Text = "";
            Refresh();
        };
        row.Children.Add(box);
        row.Children.Add(apply);
        p.Children.Add(row);
        p.Children.Add(msg);
        return p;
    }

    public PremiumPage(bool boostReady)
    {
        _boostReady = boostReady;
        var root = new StackPanel();
        root.Children.Add(Ui.Header("플랜", Platform.IsWindows
            ? "무료로도 기본 기능은 계속 쓸 수 있어요. 프로는 자동·3·4배와 품질 모드를 열고, 울트라는 여기에 6·8배, 듀얼 GPU, 게임별 자동 최적화, GPU 오버클럭까지 더해요. 산업은 울트라에 방송 모드, 업무 모드, 시작 프로그램 정리를 더해요. 효과는 게임과 PC마다 달라서, 벤치마크로 직접 확인하고 결정하세요."
            : "무료로도 기본 기능은 계속 쓸 수 있어요. 프로는 자동·3·4배와 품질 모드를 열고, 울트라는 여기에 6·8배와 듀얼 GPU까지 더해요. 효과는 게임과 PC마다 달라서, 벤치마크로 직접 확인하고 결정하세요."));

        var status = new StackPanel { Spacing = 4 };
        status.Children.Add(_state);
        status.Children.Add(_stateSub);
        root.Children.Add(Ui.Card(status));

        // comparison
        var table = new Grid { ColumnDefinitions = new ColumnDefinitions(SellsBiz ? "*,110,170,170,170" : "*,140,190,190") };
        void Cell(string text, int row, int col, bool head = false, bool accent = false)
        {
            var t = Ui.Text(text, head ? "h2" : "label");
            if (accent) t.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
            if (text is "—" or "준비 중") t.Foreground = new SolidColorBrush(Color.Parse("#5D5C60"));
            var b = new Border { Child = t, Padding = new Thickness(0, 10), BorderBrush = new SolidColorBrush(Color.Parse("#232327")),
                                 BorderThickness = new Thickness(0, row == 0 ? 0 : 1, 0, 0) };
            if (col > 0) t.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(b, row); Grid.SetColumn(b, col);
            table.Children.Add(b);
        }
        table.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", Rows.Length + 1)));
        Cell("기능", 0, 0, true); Cell("무료", 0, 1, true); Cell("프로", 0, 2, true, true); Cell("울트라", 0, 3, true, true);
        if (SellsBiz) Cell("산업", 0, 4, true, true);
        for (int i = 0; i < Rows.Length; i++)
        {
            var r = Rows[i];
            bool pending = !_boostReady && r.Feature == BoostRow;
            Cell(r.Feature, i + 1, 0);
            Cell(pending ? "준비 중" : r.Free, i + 1, 1);
            Cell(pending ? "준비 중" : r.Premium, i + 1, 2, accent: !pending && r.Premium != "—");
            Cell(pending ? "준비 중" : r.Ultra, i + 1, 3, accent: !pending && r.Ultra != "—");
            if (SellsBiz) Cell(pending ? "준비 중" : r.Biz, i + 1, 4, accent: !pending && r.Biz != "—");
        }
        root.Children.Add(Ui.Card(table));

        // buy
        var buy = new StackPanel { Spacing = 12 };
        buy.Children.Add(Ui.Text("구독하기", "h2"));
        var tierBox = new Border { Child = _tierP, HorizontalAlignment = HorizontalAlignment.Left };
        tierBox.Classes.Add("segbox");
        var tiers = SellsBiz ? new[] { ("premium", "프로"), ("ultra", "울트라"), ("biz", "산업 (모든 기능)") }
                             : new[] { ("premium", "프로"), ("ultra", "울트라 (모든 기능)") };
        foreach (var (tier, text) in tiers)
        {
            var t = new ToggleButton { Content = text, IsChecked = tier == _tier, Tag = tier };
            t.Classes.Add("seg");
            t.Click += (_, _) =>
            {
                _tier = tier;
                foreach (var o in _tierP.Children.OfType<ToggleButton>()) o.IsChecked = o == t;
                UpdateBuy();
            };
            _tierP.Children.Add(t);
        }
        buy.Children.Add(tierBox);
        var periodBox = new Border { Child = _periodP, HorizontalAlignment = HorizontalAlignment.Left };
        periodBox.Classes.Add("segbox");
        foreach (var (plan, text) in new[] { ("monthly", "월간"), ("yearly", "연간") })
        {
            var t = new ToggleButton { Content = text, IsChecked = plan == _plan, Tag = plan };
            t.Classes.Add("seg");
            t.Click += (_, _) =>
            {
                _plan = plan;
                foreach (var o in _periodP.Children.OfType<ToggleButton>()) o.IsChecked = o == t;
                UpdateBuy();
            };
            _periodP.Children.Add(t);
        }
        _price.Text = Price();
        buy.Children.Add(periodBox);
        // bundles: yearly keys for several PCs, shown only while the shop sells them
        _seatsBox.Child = _seatsP;
        _seatsBox.Classes.Add("segbox");
        foreach (var seats in SeatChoices)
        {
            var t = new ToggleButton { Content = seats == 1 ? "1대" : $"{seats}대 묶음", IsChecked = seats == _seats, Tag = seats };
            t.Classes.Add("seg");
            t.Click += (_, _) => { _seats = seats; UpdateBuy(); };
            _seatsP.Children.Add(t);
        }
        buy.Children.Add(_seatsBox);
        _price.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
        buy.Children.Add(_price);
        buy.Children.Add(_bundleNote);
        _ticketBox.Click += (_, _) => ShowPrice();
        buy.Children.Add(_ticketBox);
        buy.Children.Add(Ui.Text("월간·연간은 그 기간 동안 써요. " +
            $"스팀에서 받았다면 스팀 상점의 'SystemBoost Premium'({PriceSteamDlc}, 한 번 결제)으로 구매하세요.", "muted", wrap: true));
        // payment method: card (saved card, automatic) or bank transfer / direct payment (seller approves)
        _methodBox.Child = _methodP;
        _methodBox.Classes.Add("segbox");
        foreach (var (m, text) in new[] { ("card", "카드 결제"), ("manual", "계좌이체 · 직접 결제") })
        {
            var t = new ToggleButton { Content = text, IsChecked = m == _method, Tag = m };
            t.Classes.Add("seg");
            t.Click += (_, _) => { _method = m; UpdateBuy(); };
            _methodP.Children.Add(t);
        }
        buy.Children.Add(_methodBox);
        buy.Children.Add(_methodNote);
        _buyBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _buyBtn.IsEnabled = false;
        _buyBtn.Click += async (_, _) => await Buy();
        buy.Children.Add(_buyBtn);
        buy.Children.Add(_buyMsg);

        // order code: the buyer writes it in the payment memo so the seller can match the payment
        _orderCode.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
        _orderPanel.Children.Add(Ui.Text("주문 코드", "label"));
        var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        codeRow.Children.Add(_orderCode);
        var copyCode = Ui.Button("코드 복사", "ghost");
        copyCode.Click += async (_, _) =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null) await cb.SetTextAsync(_orderCode.Text ?? "");
            copyCode.Content = "복사했어요";
        };
        codeRow.Children.Add(copyCode);
        var reopen = Ui.Button("결제 페이지 다시 열기", "ghost");
        reopen.Click += async (_, _) =>
        {
            if (License.PendingOrders.FirstOrDefault(o => o.Code == _shown) is { Url.Length: > 0 } o) await Open(o.Url);
        };
        codeRow.Children.Add(reopen);
        _cancelBtn.Click += (_, _) =>
        {
            if (_shown is not { } code) return;
            if (!Confirmed(_cancelBtn, "한 번 더 누르면 지워요"))
            {
                _buyMsg.Text = $"주문 {code}을 이 PC에서 지울까요? 이미 결제했다면 지우지 마세요. 지운 뒤에도 판매자가 승인하면 이 화면을 열 때 키를 받아 와요.";
                return;
            }
            License.ForgetPending(code);   // kept aside: a late approval still reaches this PC
            _shown = null;
            ShowPending();
            _buyMsg.Text = $"이 PC에서 주문 {code}을 지웠어요. 이미 결제했다면 고객센터 '문의하기'로 주문 코드를 보내 주세요.";
        };
        codeRow.Children.Add(_cancelBtn);
        _orderPanel.Children.Add(codeRow);
        _orderPanel.Children.Add(_orderSub);
        // manual payments: "I paid" so the order shows up as 결제 확인 요청 in the seller's dashboard
        var paidRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        paidRow.Children.Add(_payer);
        paidRow.Children.Add(_memo);
        var paidBtn = Ui.Button("결제했어요", "primary");
        paidBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_payer.Text)) { _buyMsg.Text = "판매자가 결제를 찾을 수 있게 입금자명(결제자 이름)을 적어 주세요."; return; }
            paidBtn.IsEnabled = false;
            var err = _shown == null ? "주문이 없어요" : await License.MarkPaidAsync(_payer.Text!.Trim(), (_memo.Text ?? "").Trim(), _shown);
            paidBtn.IsEnabled = true;
            _buyMsg.Text = err ?? "판매자에게 알렸어요. 확인되면 키가 자동으로 떠요.";
            ShowPending();
        };
        paidRow.Children.Add(paidBtn);
        _paidPanel.Children.Add(Ui.Text("결제를 마쳤다면 알려 주세요", "label"));
        _paidPanel.Children.Add(paidRow);
        _orderPanel.Children.Add(_paidPanel);
        _orderPanel.Children.Add(_otherOrders);   // a new order never replaces one still waiting: each is listed
        buy.Children.Add(_orderPanel);
        _buyCard = Ui.Card(buy);
        _buyCard.IsVisible = false;
        root.Children.Add(_buyCard);

        // key
        var keyP = new StackPanel { Spacing = 10 };
        keyP.Children.Add(Ui.Text("라이선스 키", "h2"));
        keyP.Children.Add(_keyNote);
        // the key registered here, to copy for a reinstall or before it is replaced
        _currentPanel.Children.Add(Ui.Text("이 PC의 키", "label"));
        _currentPanel.Children.Add(_currentInfo);
        var currentRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        currentRow.Children.Add(_current);
        _currentShow.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_currentShow, 1);
        _currentShow.Click += (_, _) =>
        {
            _current.RevealPassword = !_current.RevealPassword;
            _currentShow.Content = _current.RevealPassword ? "숨기기" : "보기";
        };
        currentRow.Children.Add(_currentShow);
        var copyKey = Ui.Button("키 복사", "ghost");
        copyKey.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(copyKey, 2);
        copyKey.Click += async (_, _) =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null && License.KeyText() is { } k) { await cb.SetTextAsync(License.Canonical(k)); copyKey.Content = "복사했어요"; }
        };
        currentRow.Children.Add(copyKey);
        _currentPanel.Children.Add(currentRow);
        keyP.Children.Add(_currentPanel);
        keyP.Children.Add(Ui.Text("키 등록", "label"));
        var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };   // keeps a long key inside the card
        keyRow.Children.Add(_key);
        var act = Ui.Button("등록", "primary");
        act.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(act, 1);
        act.Click += async (_, _) =>
        {
            // a key that would replace a better one here (a lower plan, an earlier end, one key of a bundle, a card
            // subscription key) is asked about first; the second click registers it
            bool confirmed = _armed.ContainsKey(act);
            Disarm(act);
            act.IsEnabled = false;
            var r = await License.RegisterTypedAsync(_key.Text ?? "", confirmed);
            act.IsEnabled = true;
            if (r.Confirm != null)
            {
                Confirmed(act, "한 번 더 누르면 바꿔요");
                _msg.Text = r.Confirm;
                return;
            }
            if (r.Error != null) { _msg.Text = r.Error; return; }
            _key.Text = "";   // the key is a secret: shown again only under '이 PC의 키'
            _msg.Text = PlanStatus.Rank switch
            {
                3 => "산업이 켜졌어요. 모든 기능을 바로 쓸 수 있어요.",
                2 => "울트라가 켜졌어요. 울트라 기능을 바로 쓸 수 있어요.",
                _ => "프로가 켜졌어요. 고급 기능을 바로 쓸 수 있어요.",
            };
            Refresh();
        };
        _key.TextChanged += (_, _) => Disarm(act);
        keyRow.Children.Add(act);
        var off = Ui.Button("이 기기에서 해제", "ghost");
        off.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(off, 2);
        off.Click += (_, _) =>
        {
            if (License.KeyText() == null) { _msg.Text = "이 PC에 등록된 키가 없어요"; return; }
            if (!Confirmed(off, "한 번 더 누르면 해제해요"))
            {
                _msg.Text = "이 PC에서 키를 해제할까요? 다시 쓰려면 키가 필요하니 먼저 '이 PC의 키'에서 복사해 두세요." +
                            (License.Info?.Sub != null ? " " + License.SubscriptionGoesOn : "");
                return;
            }
            License.Deactivate();
            _msg.Text = "이 기기에서 키를 해제했어요";
            Refresh();
        };
        keyRow.Children.Add(off);
        keyP.Children.Add(keyRow);
        keyP.Children.Add(_msg);
        // an order whose key did not pop up here: ordered on another PC, cancelled here, or the app was closed too long
        keyP.Children.Add(Ui.Text("주문 코드로 키 받기", "label"));
        keyP.Children.Add(Ui.Text("결제했는데 키가 이 PC에 뜨지 않았다면 주문 코드(SB-로 시작)를 넣으세요. 승인된 주문의 키는 계좌이체 30일, 카드 결제 7일 동안 받을 수 있어요. 아직 승인 전이면 승인될 때까지 기다려요.", "muted", wrap: true));
        var claimRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        claimRow.Children.Add(_claim);
        var claimBtn = Ui.Button("키 받기", "ghost");
        claimBtn.Click += async (_, _) =>
        {
            claimBtn.IsEnabled = false;
            _claimMsg.Text = "확인하는 중…";
            var err = await License.ClaimOrderAsync(_claim.Text ?? "");
            claimBtn.IsEnabled = true;
            _claimMsg.Text = err ?? "키를 받았어요.";
            if (err == null) _claim.Text = "";
            ShowPending();
        };
        claimRow.Children.Add(claimBtn);
        keyP.Children.Add(claimRow);
        keyP.Children.Add(_claimMsg);
        _keyCard = Ui.Card(keyP);
        root.Children.Add(_keyCard);
        root.Children.Add(Ui.Card(ReferralCard()));

        Content = new ScrollViewer { Content = root };
        License.Changed += Refresh;
        Refresh();
        ShowPending();
        AttachedToVisualTree += async (_, _) => await LoadShop();   // links may change in the dashboard
        AttachedToVisualTree += async (_, _) => await LoadTicket();
        AttachedToVisualTree += async (_, _) => await License.CheckStoppedAsync();   // an order approved after it stopped being watched
    }

    /// From the plan window: that plan selected, and the place to pay in view.
    public async void Choose(string tier, string period)
    {
        _tier = tier;
        _plan = period;
        _seats = 1;
        foreach (var o in _tierP.Children.OfType<ToggleButton>()) o.IsChecked = (string?)o.Tag == tier;
        foreach (var o in _periodP.Children.OfType<ToggleButton>()) o.IsChecked = (string?)o.Tag == period;
        UpdateBuy();
        await LoadShop();
        // 기간 연장 of a key that does not renew by itself: a bank-transfer order continues from the key's end, a card
        // subscription starts on the day it is paid
        int rank = tier switch { "biz" => 3, "ultra" => 2, _ => 1 };
        if (rank == PlanStatus.Rank && PlanStatus.CanExtend && _methods["manual"].Contains(PlanId)) { _method = "manual"; UpdateBuy(); }
        var target = _buyCard.IsVisible ? _buyCard : _keyCard;
        Avalonia.Threading.Dispatcher.UIThread.Post(target.BringIntoView, Avalonia.Threading.DispatcherPriority.Background);
    }

    private async System.Threading.Tasks.Task LoadShop()
    {
        var (reachable, methods) = await License.GetShopStatusAsync();
        _methods = methods;
        UpdateBuy();
        bool closed = methods["card"].Count == 0 && methods["manual"].Count == 0;
        _buyCard.IsVisible = !closed || License.Pending != null;   // an open order stays reachable
        _keyNote.IsVisible = closed;
        _keyNote.Text = reachable || !License.ServerConfigured
            ? "지금은 구매할 수 없어요. 키를 받았다면 여기에 등록하세요."
            : "서버에 연결하지 못했어요. 키를 받았다면 여기에 등록하세요.";
    }

    private void UpdateBuy()
    {
        // a bundle is yearly and only while the shop sells it for this tier
        var bundles = SeatChoices.Where(n => n > 1 && _plan == "yearly" && _methods["manual"].Contains(IdOf(_tier, _plan, n))).ToHashSet();
        if (_seats > 1 && !bundles.Contains(_seats)) _seats = 1;
        _seatsBox.IsVisible = bundles.Count > 0;
        foreach (var t in _seatsP.Children.OfType<ToggleButton>())
        {
            var n = (int)t.Tag!;
            t.IsVisible = n == 1 || bundles.Contains(n);
            t.IsChecked = n == _seats;
        }
        _bundleNote.IsVisible = _seats > 1;
        _bundleNote.Text = $"PC마다 쓸 키 {_seats}개를 받아요. 키 하나는 PC 한 대에 넣고, 회사나 가족처럼 구매자가 관리하는 PC에만 써요. 묶음은 계좌이체 · 직접 결제로 사는 연간 키이고 할인 티켓은 쓸 수 없어요.";
        var card = _methods["card"].Contains(PlanId);
        var manual = _methods["manual"].Contains(PlanId);
        if (_method == "card" && !card && manual) _method = "manual";      // fall back to what is open for this plan
        if (_method == "manual" && !manual && card) _method = "card";
        _methodBox.IsVisible = card && manual;                              // choice only when both are open
        foreach (var t in _methodP.Children.OfType<ToggleButton>()) t.IsChecked = (string?)t.Tag == _method;
        var open = _method == "card" ? card : manual;
        // a running card subscription changes its plan instead of starting a second one next to it
        bool change = _method == "card" && _seats == 1 && License.SubscriptionActive;
        Disarm(_buyBtn);
        _buyBtn.IsEnabled = open;
        _buyBtn.Content = !open ? "결제 준비 중" : change ? "구독 플랜 바꾸기" : _method == "card" ? "카드로 결제하기" : "결제하기";
        _ticketBox.IsVisible = _ticketPercent > 0 && open && _method == "manual" && _seats == 1;   // a card checkout and a bundle cannot take it
        ShowPrice();
        _methodNote.IsVisible = open;
        if (change)
            _methodNote.Text = "지금 쓰는 카드 구독의 플랜을 바꿔요. 구독이 하나 더 생기지 않고, 새 플랜의 키는 몇 분 안에 자동으로 받아 와요. 금액 차이는 카드 결제 서비스가 정산해요.";
        else if (_method == "card")
            _methodNote.Text = "카드를 등록하면 해지할 때까지 기간마다 자동으로 결제되고, 앱을 켜면 새 기간의 키를 자동으로 받아 와요. 해지는 '고객센터 → 구독 관리'나 결제 메일의 구독 관리 링크에서 언제든 할 수 있어요. " +
                              "카드 구독은 결제한 날부터 시작해요(지금 키의 남은 기간은 더해지지 않아요).";
        else
        {
            var note = "판매자 결제 페이지에서 결제한 뒤 '결제했어요'를 누르면, 판매자가 확인하고 키를 보내 줘요.";
            if (License.ExtendsFrom(PlanId, "manual") is DateTime end)
                note += " " + $"지금 키가 끝나는 {end.ToLocalTime():yyyy-MM-dd}부터 이어서 새 기간이 더해져요.";
            else if (_seats == 1)
                note += " 키의 기간은 판매자가 승인한 날부터 시작해요.";
            if (License.SubscriptionActive) note += " " + License.SubscriptionGoesOn;
            _methodNote.Text = note;
        }
    }

    private void ShowPending()
    {
        var all = License.PendingOrders;   // newest first
        var o = all.FirstOrDefault(x => x.Code == _shown) ?? all.FirstOrDefault();
        if (o?.Code != _shown) Disarm(_cancelBtn);
        _shown = o?.Code;
        _orderPanel.IsVisible = o != null;
        _otherOrders.Children.Clear();
        _otherOrders.IsVisible = all.Length > 1;
        if (o == null) return;
        _orderCode.Text = o.Code;
        var plan = o.Plan.Length > 0 ? PlanLabel(o.Plan) : "주문 코드로 확인한";   // a code typed in: the plan is not known here
        var auto = o.Method is "card" or "auto";
        _paidPanel.IsVisible = !auto && o.Status != "paid";
        _orderSub.Text = auto
            ? $"{plan} 카드 결제 · 결제창에서 결제를 마치면 키가 자동으로 만들어져 이 앱에 떠요. 앱을 껐다 켜도 이어서 기다려요."
            : o.Status == "paid"
                ? $"{plan} 주문 · 결제 확인 요청을 보냈어요. 판매자가 확인하면 키가 자동으로 떠요(시간이 걸릴 수 있어요). 앱을 껐다 켜도 주문한 날부터 90일까지 기다려요."
                : $"{plan} 주문 · 결제할 때 요청사항(메모)에 이 코드를 꼭 적어 주세요. 결제를 마친 뒤 아래에 입금자명을 적고 '결제했어요'를 누르면 판매자가 확인해요.";
        if (o.Extends is DateTime end) _orderSub.Text += "\n" + $"지금 키가 끝나는 {end.ToLocalTime():yyyy-MM-dd}부터 이어서 새 기간이 더해져요.";
        if (all.Length < 2) return;
        _otherOrders.Children.Add(Ui.Text("기다리는 다른 주문 · 누르면 그 주문을 보여 줘요", "label"));
        foreach (var other in all.Where(x => x.Code != o.Code))
        {
            var b = Ui.Button(other.Code + " · " + (other.Plan.Length > 0 ? PlanLabel(other.Plan) : "주문") + (other.Status == "paid" ? " · 결제 확인 중" : ""), "ghost");
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Click += (_, _) => { _shown = other.Code; ShowPending(); };
            _otherOrders.Children.Add(b);
        }
    }

    private async System.Threading.Tasks.Task Open(string url)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top != null) await top.Launcher.LaunchUriAsync(new Uri(url));
    }

    private async System.Threading.Tasks.Task LoadTicket()
    {
        _ticketPercent = 0;
        if (Account.SignedIn && (await Account.ProfileAsync()).profile is { TicketPercent: int percent } p)
        {
            _ticketPercent = percent;
            _ticketBox.Content = $"할인 티켓 {percent}% 쓰기 ({p.TicketExpires?.LocalDateTime:yyyy-MM-dd}까지)";
            _ticketBox.IsChecked = true;
        }
        UpdateBuy();
    }

    private bool UseTicket => _ticketBox.IsVisible && _ticketBox.IsChecked == true;

    private void ShowPrice()
    {
        var text = Price();
        if (UseTicket)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, @"₩([\d,]+)");
            var won = int.Parse(m.Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
            var sale = won * (100 - _ticketPercent) / 100;
            text = text.Replace(m.Value, "₩" + sale.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        }
        _price.Text = text;
    }

    private string Price()
    {
        if (_seats > 1 && Bundles.TryGetValue((_tier, _seats), out var b))
            return $"₩{b.total.ToString("N0", CultureInfo.InvariantCulture)} / 년 · {_seats}대 (대당 ₩{b.each.ToString("N0", CultureInfo.InvariantCulture)})";
        return (_tier, _plan) switch
        {
            ("biz", "monthly") => PriceBizMonthly, ("biz", _) => PriceBizYearly,
            ("ultra", "monthly") => PriceUltraMonthly, ("ultra", _) => PriceUltraYearly,
            (_, "monthly") => PriceMonthly, _ => PriceYearly,
        };
    }

    /// "산업 연간 10대 묶음" for biz_yearly_x10, as orders name plans.
    internal static string PlanLabel(string plan)
    {
        var tier = plan.StartsWith("ultra_") ? "울트라 " : plan.StartsWith("biz_") ? "산업 " : "프로 ";
        var seats = Regex.Match(plan, @"_x(\d+)$");
        var period = Regex.Replace(Regex.Replace(plan, "^(ultra|biz)_", ""), @"_x\d+$", "") switch { "monthly" => "월간", "lifetime" => "평생", _ => "연간" };
        return tier + period + (seats.Success ? $" {seats.Groups[1].Value}대 묶음" : "");
    }

    /// Gets an order code, shows it, opens the seller's payment page. The key arrives later (License.KeyArrived).
    private async System.Threading.Tasks.Task Buy()
    {
        if (_method == "card" && _seats == 1 && License.SubscriptionActive)
        {   // a running card subscription: its plan changes, a second checkout would bill twice
            if (!Confirmed(_buyBtn, "한 번 더 누르면 플랜을 바꿔요"))
            {
                _buyMsg.Text = $"지금 카드 구독을 {PlanLabel(PlanId)}(으)로 바꿔요. 구독이 하나 더 생기지 않아요.";
                return;
            }
            _buyBtn.IsEnabled = false;
            _buyMsg.Text = "플랜을 바꾸는 중…";
            var error = await License.ChangeSubscriptionAsync(PlanId);
            UpdateBuy();
            _buyMsg.Text = error ?? "플랜 변경을 요청했어요. 새 플랜의 키는 몇 분 안에 자동으로 받아 와요.";
            return;
        }
        if (License.PendingOrders.FirstOrDefault(o => o.Plan == PlanId && o.Method == _method && o.Ticket == UseTicket && o.Url.Length > 0 && o.Status != "paid") is { } prev)
        {   // same plan already ordered and not paid yet: reuse the code instead of making a new one
            _shown = prev.Code;
            ShowPending();
            _buyMsg.Text = $"이미 만든 주문 {prev.Code}이 있어요. 같은 주문으로 결제하세요.";
            await Open(prev.Url);
            return;
        }
        // an order reported as paid keeps waiting next to a new one: said before a second payment
        if (License.PaidPending is { } paid && !Confirmed(_buyBtn, "한 번 더 누르면 새로 주문해요"))
        {
            _buyMsg.Text = $"결제 확인을 기다리는 주문 {paid.Code}이 있어요. 새로 주문해도 그 주문은 계속 기다려요. 같은 플랜을 두 번 결제하지 않게 주의하세요.";
            return;
        }
        Disarm(_buyBtn);
        _buyBtn.IsEnabled = false;
        _buyMsg.Text = "주문을 만드는 중…";
        var (code, url, err) = await License.CreateOrderAsync(PlanId, _method, UseTicket);
        UpdateBuy();
        if (code == null || url == null) { _buyMsg.Text = err ?? "주문을 만들지 못했어요."; return; }
        _shown = code;
        ShowPending();
        if (License.PendingOrders.FirstOrDefault(o => o.Code == code)?.Method is "card" or "auto")
            _buyMsg.Text = "카드 결제창이 열려요. 결제가 끝나면 키가 자동으로 떠요.";
        else
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null) await cb.SetTextAsync(code);
            _buyMsg.Text = $"주문 코드 {code}를 복사해 뒀어요. 결제 페이지의 요청사항(메모)에 붙여 넣어 주세요.";
        }
        await Open(url);
    }

    private void Refresh()
    {
        var info = License.Info is { } i && (i.Expires == null || i.Expires > DateTime.UtcNow) ? i : null;   // a key that ran out is shown below only
        if (License.IsPremium)
        {
            _state.Text = PlanStatus.Rank switch { 3 => "산업 사용 중 ★", 2 => "울트라 사용 중 ★", _ => "프로 사용 중 ★" };
            _stateSub.Text = info?.Expires is DateTime e ? $"{e.ToLocalTime():yyyy-MM-dd}까지 · {info.Email}" : "스팀 DLC 또는 기간 없는 키";
        }
        else
        {
            _state.Text = "무료 플랜";
            _stateSub.Text = _boostReady ? "프레임 생성 2배, 업스케일, 게임 부스트, 모니터링을 쓸 수 있어요."
                                         : "프레임 생성 2배, 업스케일, 모니터링을 쓸 수 있어요.";
        }
        if (License.Notice != null) _msg.Text = License.Notice;
        ShowCurrent();
        ShowPending();
        UpdateBuy();   // a card subscription key arriving or ending changes what the buy button does
    }

    private void ShowCurrent()
    {
        var raw = License.KeyText();
        var info = License.Info;
        _currentPanel.IsVisible = raw != null && info != null;
        if (raw == null || info == null) { _current.Text = ""; return; }
        var text = License.Canonical(raw);
        if (_current.Text != text) { _current.Text = text; _current.RevealPassword = false; _currentShow.Content = "보기"; }
        var parts = new List<string> { License.Describe(info) };
        if (info.Sub != null) parts.Add("카드 구독");
        if (info.Expires is DateTime e && e < DateTime.UtcNow) parts.Add("기간이 끝났어요");
        _currentInfo.Text = string.Join(" · ", parts);
    }
}
