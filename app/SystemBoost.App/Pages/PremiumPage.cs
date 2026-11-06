// Premium subscription window: plan comparison, monthly / yearly, buy, enter key.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SystemBoost.App.Services;
using System;
using System.Linq;

namespace SystemBoost.App.Pages;

public sealed class PremiumPage : UserControl
{
    // Prices shown in the app. TODO: set real prices (must match the shop / Steam).
    public const string PriceMonthly = "₩2,900 / 월";
    public const string PriceYearly = "₩19,900 / 년";
    public const string PriceLifetime = "₩49,900 · 한 번 결제";
    public const string PriceUltraMonthly = "₩4,900 / 월";
    public const string PriceUltraYearly = "₩34,900 / 년";
    public const string PriceUltraLifetime = "₩89,900 · 한 번 결제";
    public const string PriceSteamDlc = "₩14,000";

    private readonly TextBlock _state = Ui.Text("", "h2");
    private readonly TextBlock _stateSub = Ui.Text("", "sub", wrap: true);
    private readonly TextBlock _price = Ui.Text(PriceMonthly, "big");
    private readonly TextBox _key = new() { Watermark = "XXXX.XXXX 형식 라이선스 키", MinWidth = 360 };
    private readonly TextBlock _msg = Ui.Text("", "muted", wrap: true);
    private readonly TextBlock _buyMsg = Ui.Text("", "muted", wrap: true);
    private string _plan = "yearly";
    private bool _ultra;
    private string PlanId => _ultra ? "ultra_" + _plan : _plan;
    private readonly Button _buyBtn = Ui.Button("결제 준비 중", "primary");
    private System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> _methods = new() { ["card"] = new(), ["manual"] = new() };
    private string _method = "card";
    private readonly StackPanel _methodP = new() { Orientation = Orientation.Horizontal };
    private readonly Border _methodBox = new() { HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private readonly TextBlock _methodNote = Ui.Text("", "muted", wrap: true);
    private readonly StackPanel _paidPanel = new() { Spacing = 8 };
    private readonly TextBox _payer = new() { Watermark = "입금자명 / 결제자 이름", MinWidth = 200 };
    private readonly TextBox _memo = new() { Watermark = "메모 (선택: 결제 수단, 시각 등)", MinWidth = 240 };
    private readonly StackPanel _orderPanel = new() { Spacing = 8, IsVisible = false };
    private readonly TextBlock _orderCode = Ui.Text("", "big");
    private readonly TextBlock _orderSub = Ui.Text("", "muted", wrap: true);
    private readonly TextBlock _keyNote = Ui.Text("", "muted", wrap: true);
    private readonly Border _buyCard;   // shown only while something is on sale

    // Must match License.UltraOnly. windowsOnly: macOS does not allow these, see MacBoostBackend.
    private static readonly (string feature, string free, string premium, string ultra, bool windowsOnly)[] AllRows =
    {
        ("프레임 생성 배수", "2배", "자동 · 2 · 3 · 4배", "자동 · 2 · 3 · 4 · 6 · 8배", false),
        ("품질 모드", "저사양 · 성능 · 균형", "저사양 · 성능 · 균형 · 품질", "저사양 · 성능 · 균형 · 품질", false),
        ("업스케일링 + 선명화", "●", "●", "●", false),
        ("게임 부스트", "●", "●", "●", false),
        ("모니터링 · 벤치마크", "●", "●", "●", false),
        ("자원 분배 (사용량 그래프 · 우선/낮춤)", "●", "●", "●", false),
        ("자원 분배 · CPU 최대 사용량 제한", "—", "●", "●", true),
        ("듀얼 GPU (내장 그래픽에 생성 맡기기)", "—", "—", "●", false),
        ("게임별 자동 최적화 (측정해서 효과 있는 것만)", "—", "—", "●", true),
        ("GPU 오버클럭 / 전력 제한 (Windows, NVIDIA)", "—", "—", "●", true),
    };
    private static readonly (string feature, string free, string premium, string ultra, bool windowsOnly)[] Rows =
        AllRows.Where(r => Platform.IsWindows || !r.windowsOnly).ToArray();
    private const string BoostRow = "게임 부스트";
    private readonly bool _boostReady;

    public PremiumPage(bool boostReady)
    {
        _boostReady = boostReady;
        var root = new StackPanel();
        root.Children.Add(Ui.Header("프리미엄", $"무료로도 기본 기능은 계속 쓸 수 있어요. 프리미엄은 자동·3·4배와 품질 모드를 열고, 울트라는 여기에 {(Platform.IsWindows ? "6·8배, 듀얼 GPU, 게임별 자동 최적화, GPU 오버클럭" : "6·8배와 듀얼 GPU")}까지 더해요. 효과는 게임과 PC마다 달라서, 벤치마크로 직접 확인하고 결정하세요."));

        var status = new StackPanel { Spacing = 4 };
        status.Children.Add(_state);
        status.Children.Add(_stateSub);
        root.Children.Add(Ui.Card(status));

        // comparison
        var table = new Grid { ColumnDefinitions = new ColumnDefinitions("*,140,190,190") };
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
        Cell("기능", 0, 0, true); Cell("무료", 0, 1, true); Cell("프리미엄", 0, 2, true, true); Cell("울트라", 0, 3, true, true);
        for (int i = 0; i < Rows.Length; i++)
        {
            bool pending = !_boostReady && Rows[i].feature == BoostRow;
            Cell(Rows[i].feature, i + 1, 0);
            Cell(pending ? "준비 중" : Rows[i].free, i + 1, 1);
            Cell(pending ? "준비 중" : Rows[i].premium, i + 1, 2, accent: !pending && Rows[i].premium != "—");
            Cell(pending ? "준비 중" : Rows[i].ultra, i + 1, 3, accent: !pending && Rows[i].ultra != "—");
        }
        root.Children.Add(Ui.Card(table));

        // buy
        var buy = new StackPanel { Spacing = 12 };
        buy.Children.Add(Ui.Text("구독하기", "h2"));
        var tierP = new StackPanel { Orientation = Orientation.Horizontal };
        var tierBox = new Border { Child = tierP, HorizontalAlignment = HorizontalAlignment.Left };
        tierBox.Classes.Add("segbox");
        foreach (var (ultra, text) in new[] { (false, "프리미엄"), (true, "울트라 (모든 기능)") })
        {
            var t = new ToggleButton { Content = text, IsChecked = ultra == _ultra };
            t.Classes.Add("seg");
            t.Click += (_, _) =>
            {
                _ultra = ultra;
                foreach (var o in tierP.Children.OfType<ToggleButton>()) o.IsChecked = o == t;
                _price.Text = Price();
                UpdateBuy();
            };
            tierP.Children.Add(t);
        }
        buy.Children.Add(tierBox);
        var periodP = new StackPanel { Orientation = Orientation.Horizontal };
        var periodBox = new Border { Child = periodP, HorizontalAlignment = HorizontalAlignment.Left };
        periodBox.Classes.Add("segbox");
        foreach (var (plan, text) in new[] { ("monthly", "월간"), ("yearly", "연간"), ("lifetime", "평생") })
        {
            var t = new ToggleButton { Content = text, IsChecked = plan == _plan };
            t.Classes.Add("seg");
            t.Click += (_, _) =>
            {
                _plan = plan;
                foreach (var o in periodP.Children.OfType<ToggleButton>()) o.IsChecked = o == t;
                _price.Text = Price();
                UpdateBuy();
            };
            periodP.Children.Add(t);
        }
        _price.Text = Price();
        buy.Children.Add(periodBox);
        _price.Foreground = new SolidColorBrush(Color.Parse("#FF7A1A"));
        buy.Children.Add(_price);
        buy.Children.Add(Ui.Text("월간·연간은 그 기간 동안, 평생은 한 번 결제로 계속 써요. " +
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
            if (License.Pending is { Url.Length: > 0 } o) await Open(o.Url);
        };
        codeRow.Children.Add(reopen);
        var cancel = Ui.Button("주문 취소", "ghost");
        cancel.Click += (_, _) => { License.ForgetPending(); ShowPending(); _buyMsg.Text = "이 기기에서 주문을 지웠어요. 이미 결제했다면 결제 메모의 코드로 판매자에게 문의하세요."; };
        codeRow.Children.Add(cancel);
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
            var err = await License.MarkPaidAsync(_payer.Text!.Trim(), (_memo.Text ?? "").Trim());
            paidBtn.IsEnabled = true;
            _buyMsg.Text = err ?? "판매자에게 알렸어요. 확인되면 키가 자동으로 떠요.";
            ShowPending();
        };
        paidRow.Children.Add(paidBtn);
        _paidPanel.Children.Add(Ui.Text("결제를 마쳤다면 알려 주세요", "label"));
        _paidPanel.Children.Add(paidRow);
        _orderPanel.Children.Add(_paidPanel);
        buy.Children.Add(_orderPanel);
        _buyCard = Ui.Card(buy);
        _buyCard.IsVisible = false;
        root.Children.Add(_buyCard);

        // key
        var keyP = new StackPanel { Spacing = 10 };
        keyP.Children.Add(Ui.Text("라이선스 키", "h2"));
        keyP.Children.Add(_keyNote);
        var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };   // keeps a long key inside the card
        keyRow.Children.Add(_key);
        var act = Ui.Button("등록", "primary");
        act.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(act, 1);
        act.Click += (_, _) =>
        {
            var err = License.Activate(_key.Text ?? "");
            _msg.Text = err ?? (License.IsUltra ? "울트라가 켜졌어요. 모든 기능을 바로 쓸 수 있어요." : "프리미엄이 켜졌어요. 고급 기능을 바로 쓸 수 있어요.");
            Refresh();
        };
        keyRow.Children.Add(act);
        var off = Ui.Button("이 기기에서 해제", "ghost");
        off.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(off, 2);
        off.Click += (_, _) => { License.Deactivate(); _msg.Text = "이 기기에서 키를 해제했어요"; Refresh(); };
        keyRow.Children.Add(off);
        keyP.Children.Add(keyRow);
        keyP.Children.Add(_msg);
        root.Children.Add(Ui.Card(keyP));

        Content = new ScrollViewer { Content = root };
        License.Changed += Refresh;
        Refresh();
        ShowPending();
        AttachedToVisualTree += async (_, _) => await LoadShop();   // links may change in the dashboard
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
        var card = _methods["card"].Contains(PlanId);
        var manual = _methods["manual"].Contains(PlanId);
        if (_method == "card" && !card && manual) _method = "manual";      // fall back to what is open for this plan
        if (_method == "manual" && !manual && card) _method = "card";
        _methodBox.IsVisible = card && manual;                              // choice only when both are open
        foreach (var t in _methodP.Children.OfType<ToggleButton>()) t.IsChecked = (string?)t.Tag == _method;
        var open = _method == "card" ? card : manual;
        _buyBtn.IsEnabled = open;
        _buyBtn.Content = !open ? "결제 준비 중" : _method == "card" ? "카드로 결제하기" : "결제하기";
        _methodNote.IsVisible = open;
        _methodNote.Text = _method == "card"
            ? (_plan == "lifetime" ? "카드로 한 번 결제하면 키가 바로 떠요." : "카드를 등록하면 기간마다 자동으로 결제되고 키도 자동으로 연장돼요. 해지는 결제 메일의 구독 관리 링크에서 언제든 할 수 있어요.")
            : "판매자 결제 페이지에서 결제한 뒤 '결제했어요'를 누르면, 판매자가 확인하고 키를 보내 줘요. 기간이 끝나기 전에 다시 결제하면 이어서 써요.";
    }

    private void ShowPending()
    {
        var o = License.Pending;
        _orderPanel.IsVisible = o != null;
        if (o == null) return;
        _orderCode.Text = o.Code;
        var period = o.Plan.Replace("ultra_", "") switch { "monthly" => "월간", "lifetime" => "평생", _ => "연간" };
        var plan = o.Plan.StartsWith("ultra_") ? "울트라 " + period : period;
        var auto = o.Method is "card" or "auto";
        _paidPanel.IsVisible = !auto && o.Status != "paid";
        _orderSub.Text = auto
            ? $"{plan} 카드 결제 · 결제창에서 결제를 마치면 키가 자동으로 만들어져 이 앱에 떠요. 앱을 껐다 켜도 이어서 기다려요."
            : o.Status == "paid"
                ? $"{plan} 주문 · 결제 확인 요청을 보냈어요. 판매자가 확인하면 키가 자동으로 떠요(시간이 걸릴 수 있어요). 앱을 껐다 켜도 이어서 기다려요."
                : $"{plan} 주문 · 결제할 때 요청사항(메모)에 이 코드를 꼭 적어 주세요. 결제를 마친 뒤 아래에 입금자명을 적고 '결제했어요'를 누르면 판매자가 확인해요.";
    }

    private async System.Threading.Tasks.Task Open(string url)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top != null) await top.Launcher.LaunchUriAsync(new Uri(url));
    }

    private string Price() => (_ultra, _plan) switch
    {
        (false, "monthly") => PriceMonthly, (false, "lifetime") => PriceLifetime, (false, _) => PriceYearly,
        (true, "monthly") => PriceUltraMonthly, (true, "lifetime") => PriceUltraLifetime, _ => PriceUltraYearly,
    };

    /// Gets an order code, shows it, opens the seller's payment page. The key arrives later (License.KeyArrived).
    private async System.Threading.Tasks.Task Buy()
    {
        if (License.Pending is { } prev && prev.Plan == PlanId && prev.Method == _method && prev.Url.Length > 0)
        {   // same plan already ordered: reuse the code instead of making a new one
            _buyMsg.Text = "이미 만든 주문이 있어요. 같은 주문으로 결제하세요.";
            await Open(prev.Url);
            return;
        }
        _buyBtn.IsEnabled = false;
        _buyMsg.Text = "주문을 만드는 중…";
        var (code, url, err) = await License.CreateOrderAsync(PlanId, _method);
        UpdateBuy();
        if (code == null || url == null) { _buyMsg.Text = err ?? "주문을 만들지 못했어요."; return; }
        ShowPending();
        if (License.Pending?.Method is "card" or "auto")
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
        var info = License.Info;
        if (License.IsPremium)
        {
            _state.Text = License.IsUltra ? "울트라 사용 중 ★" : "프리미엄 사용 중 ★";
            _stateSub.Text = info?.Expires is DateTime e ? $"{e.ToLocalTime():yyyy-MM-dd}까지 · {info.Email}" : "스팀 DLC 또는 기간 없는 키";
        }
        else
        {
            _state.Text = "무료 플랜";
            _stateSub.Text = _boostReady ? "프레임 생성 2배, 업스케일, 게임 부스트, 모니터링을 쓸 수 있어요."
                                         : "프레임 생성 2배, 업스케일, 모니터링을 쓸 수 있어요.";
        }
        if (License.Notice != null) _msg.Text = License.Notice;
        ShowPending();
    }
}
