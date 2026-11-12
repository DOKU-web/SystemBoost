// 고객센터: frequent questions, a question to the seller, this PC's questions with their replies, a refund request,
// the seller's details and, for a card subscription key, its management page. Same on Windows and macOS.
// Nothing here decides whether a refund is due: the page quotes the licence agreement (제9조) and the seller decides.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SystemBoost.App.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SystemBoost.App.Pages;

public sealed class SupportPage : UserControl
{
    private enum Tab { Faq, Ask, Mine, Refund }

    // The required items: what is kept, why, for how long, and what refusing means. Must match the retention in
    // server/license-worker (/support/ticket) and the consent text of /help. The optional items (contact, app
    // details) are explained next to their own fields, not here. One line per entry in lang.en.json.
    private static readonly string[] ConsentLines =
    {
        "보내면 보관하는 필수 항목: 문의 종류, 제목, 내용, 판매자의 답, 동의한 시각, 로그인 중이면 회원 아이디, 환불 요청이면 키 식별 번호와 주문 코드.",
        "환불 요청이면 판매자 서버에 있는 주문 · 키 정보(플랜, 키를 받은 날, 입금자명 등)도 문의에 붙여 보관해요. 환불 요청에 함께 가는 키는 서버가 확인에만 쓰고 저장하지 않아요.",
        "이유: 문의에 답하고 결제와 환불을 처리한 기록을 남기려고요.",
        "기간: 결제 · 환불 문의는 3년, 그 밖의 문의는 1년 동안 판매자 서버에 보관해요. 기간은 문의가 마지막으로 바뀐 날부터 세요.",
        "IP 주소는 문의에 남기지 않아요. 너무 많은 요청을 막으려고 IP 주소로 만든 해시값을 서버 메모리와 Cloudflare 캐시에 1시간까지 둬요.",
        "동의하지 않으면 문의를 보낼 수 없어요. 앱은 그대로 쓸 수 있어요.",
    };

    // Next to each optional item: sent only when filled in or ticked.
    private const string ContactNote = "연락처는 선택이에요. 적은 경우에만 보내고 문의와 같은 기간 보관해요. 답은 '내 문의'에만 와요.";

    private SupportInfo _info = Support.Fallback;
    private bool _loaded;                 // the server was asked at least once
    private Tab _tab = Tab.Faq;

    private readonly Border _banner = new() { IsVisible = false };
    private readonly TextBlock _bannerText = Ui.Text("", "label", wrap: true);
    private readonly Dictionary<Tab, Button> _tabButtons = new();
    private readonly Border _mineBadge = Ui.Badge("");
    private readonly ContentControl _body = new();
    private readonly ContentControl _subscription = new();
    private readonly ContentControl _seller = new();
    private readonly DispatcherTimer _timer = new() { Interval = Support.PageEvery };

    // 문의하기 (built once, so typed text stays when the tab changes)
    private readonly StackPanel _askPanel = new() { Spacing = 10 };
    private readonly StackPanel _askCats = new() { Orientation = Orientation.Horizontal };
    private string? _askCat;
    private readonly TextBox _askSubject = Input($"제목 ({Support.MaxSubject}자까지)", Support.MaxSubject);
    private readonly TextBox _askText = Area($"내용 ({Support.MaxText}자까지)", Support.MaxText);
    private readonly TextBlock _askCount = Counter();
    private readonly TextBox _askContact = Input($"판매자가 따로 연락할 연락처 (선택, {Support.MaxContact}자까지)", Support.MaxContact);
    private readonly CheckBox _askDiag = new() { Content = "앱 정보 함께 보내기 (선택)", IsChecked = false };
    private readonly StackPanel _diagLines = new() { Spacing = 2 };
    private readonly CheckBox _askConsent = new() { Content = "위 필수 항목을 보관하는 데 동의해요 (필수)" };
    private readonly Button _askSend = Ui.Button("보내기", "primary");
    private readonly TextBlock _askMsg = Message();
    private bool _askSending;             // LoadInfo must not turn the button back on during a send

    // 환불 요청
    private readonly StackPanel _refundPanel = new() { Spacing = 10 };
    private readonly ContentControl _rfFacts = new();
    private readonly ContentControl _rfCard = new();
    private readonly TextBox _rfOrder = Input("주문 코드 SB-XXXX-XXXX (있으면)", 12);
    private readonly ComboBox _rfReason = new() { PlaceholderText = "환불 사유 고르기" };
    private string? _rfReasonId;
    private readonly TextBox _rfText = Area($"자세한 내용 (선택, {Support.MaxText}자까지)", Support.MaxText);
    private readonly TextBlock _rfCount = Counter();
    private readonly TextBox _rfContact = Input($"판매자가 따로 연락할 연락처 (선택, {Support.MaxContact}자까지)", Support.MaxContact);
    private readonly CheckBox _rfConsent = new() { Content = "위 필수 항목을 보관하는 데 동의해요 (필수)" };
    private readonly Button _rfSend = Ui.Button("환불 요청 보내기", "primary");
    private readonly TextBlock _rfMsg = Message();
    private bool _rfSending;

    // 내 문의: one ticket at a time; the reply box stays in place when the ticket is drawn again
    private string? _thread;
    private SupportTicket? _ticket;
    private string? _threadNote, _threadError;
    private int _shown;                   // a slower answer for an older ticket must not land on this one
    private readonly StackPanel _threadView = new() { Spacing = 10 };
    private readonly StackPanel _threadTop = new() { Spacing = 10 };
    private readonly StackPanel _replyArea = new() { Spacing = 8 };
    private readonly TextBlock _replyState = Ui.Text("", "muted", wrap: true);
    private readonly TextBox _replyBox = Area($"답장 ({Support.MaxText}자까지)", Support.MaxText);
    private readonly TextBlock _replyCount = Counter();
    private readonly Button _replySend = Ui.Button("답장 보내기", "primary");
    private readonly Button _closeTicket = Ui.Button("문의 닫기", "ghost");
    private readonly TextBlock _replyMsg = Message();

    public SupportPage()
    {
        var root = new StackPanel { Spacing = 20 };
        root.Children.Add(Ui.Header("고객센터", "궁금한 점이나 환불 요청을 판매자에게 보내요. 답은 '내 문의'에 오고, 앱이 켜져 있으면 알림으로도 알려 줘요."));
        _banner.Classes.Add("note");
        _banner.Child = _bannerText;
        root.Children.Add(_banner);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void AddTab(Tab tab, string text)
        {
            var b = new Button();
            b.Classes.Add("ghost");
            if (tab == Tab.Mine)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(_mineBadge);
                b.Content = line;
                AutomationProperties.SetName(b, text);
            }
            else b.Content = text;
            b.Click += (_, _) =>
            {
                if (tab == Tab.Mine) _thread = null;   // the tab itself goes back to the list
                _tab = tab;
                ShowTab();
            };
            _tabButtons[tab] = b;
            tabs.Children.Add(b);
        }
        AddTab(Tab.Faq, "자주 묻는 질문");
        AddTab(Tab.Ask, "문의하기");
        AddTab(Tab.Mine, "내 문의");
        AddTab(Tab.Refund, "환불 요청");
        root.Children.Add(tabs);
        root.Children.Add(Ui.Card(_body));
        root.Children.Add(_subscription);
        root.Children.Add(_seller);

        BuildAsk();
        BuildRefund();
        BuildList();
        BuildThread();
        FillCategories();
        FillReasons();
        ShowSeller();
        ShowSubscription();
        ShowKeyParts();
        UpdateBadge();
        ShowTab();

        Content = new ScrollViewer { Content = root };
        _timer.Tick += async (_, _) => { if (Awake) await Poll(); };
        AttachedToVisualTree += (_, _) =>
        {
            Support.Changed += OnTicketsChanged;
            License.Changed += OnLicenseChanged;
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window != null) _window.Activated += OnWindowActivated;
            Support.Reload();   // tickets another copy of the app sent meanwhile
            OnTicketsChanged();
            OnLicenseChanged();
            _ = LoadInfo();      // asked again only every 10 minutes
            _timer.Interval = Support.Every(Support.PageEvery);
            _timer.Start();
            if (Awake && Support.MayLook) _ = Poll();   // a look on opening, unless one went out within the page interval or the server asked to wait
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _timer.Stop();
            if (_window != null) _window.Activated -= OnWindowActivated;
            _window = null;
            Support.Changed -= OnTicketsChanged;
            License.Changed -= OnLicenseChanged;
        };
    }

    // ------------------------------------------------------------ looking for replies while the page is shown
    private Window? _window;

    /// Looks only while someone can see the page: the main window active and not minimized. Otherwise it pauses.
    private bool Awake => _window is { IsActive: true } w && w.WindowState != WindowState.Minimized;

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (Support.MayLook) _ = Poll();   // back after a pause that missed a look
    }

    /// One batch look. The ticket on screen is read whole only when the look says it changed, or when the user asked.
    private async Task<string?> Poll(bool manual = false)
    {
        var reading = _tab == Tab.Mine ? _thread : null;
        var before = reading != null ? Support.Find(reading) : null;
        var error = await Support.RefreshAsync(manual ? Support.Scope.All : Support.Scope.Page, reading);
        _timer.Interval = Support.Every(Support.PageEvery);   // longer after a 429
        if (reading != null && reading == _thread && _tab == Tab.Mine && (manual || before != Support.Find(reading)))
            await LoadThread();
        return error;
    }

    private async Task Refresh(Button button, TextBlock message)
    {
        button.IsEnabled = false;
        message.Text = "확인하는 중…";
        var error = await Poll(manual: true);
        button.IsEnabled = true;
        message.Text = error ?? "방금 확인했어요.";
    }

    // ------------------------------------------------------------ helpers
    private static TextBox Input(string hint, int max) => new() { Watermark = hint, MaxLength = max };

    private static TextBox Area(string hint, int max) => new()
    {
        Watermark = hint, MaxLength = max, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 320,
    };

    private static TextBlock Counter()
    {
        var t = Ui.Text($"0 / {Support.MaxText}", "muted");
        t.HorizontalAlignment = HorizontalAlignment.Right;
        return t;
    }

    private static void Count(TextBox box, TextBlock counter) =>
        box.TextChanged += (_, _) => counter.Text = $"{(box.Text ?? "").Length} / {Support.MaxText}";

    private static TextBlock Message() => Ui.Text("", "muted", wrap: true);

    private static Control Left(Control c)
    {
        c.HorizontalAlignment = HorizontalAlignment.Left;
        return c;
    }

    private static Control Consent(CheckBox box)
    {
        var p = new StackPanel { Spacing = 6 };
        foreach (var line in ConsentLines) p.Children.Add(Ui.Text(line, "muted", wrap: true));
        p.Children.Add(Ui.Text("자세한 내용은 개인정보 처리방침에서 볼 수 있어요.", "muted", wrap: true));
        var status = Ui.Text("", "muted", wrap: true);
        status.IsVisible = false;
        p.Children.Add(Left(Ui.PrivacyButton(status)));
        p.Children.Add(status);
        p.Children.Add(box);
        return p;
    }

    private static Grid Facts(IEnumerable<(string title, string value)> rows)
    {
        var list = rows.ToList();
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", Math.Max(1, list.Count)))) };
        for (int i = 0; i < list.Count; i++)
        {
            var left = Ui.Text(list[i].title, "muted");
            var right = Ui.Text(list[i].value, "label", wrap: true);
            right.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetRow(left, i);
            Grid.SetRow(right, i);
            Grid.SetColumn(right, 1);
            g.Children.Add(left);
            g.Children.Add(right);
        }
        return g;
    }

    private string CatLabel(string id) =>
        (_info.Categories.FirstOrDefault(c => c.Id == id) ?? Support.BuiltInCategories.FirstOrDefault(c => c.Id == id))?.Label ?? id;

    private string ReasonLabel(string id) =>
        (_info.RefundReasons.FirstOrDefault(c => c.Id == id) ?? Support.BuiltInReasons.FirstOrDefault(c => c.Id == id))?.Label ?? id;

    /// A key's tier (premium, ultra, biz) or an order's plan id ([tier_]period[_xN], as server/license-worker sells
    /// them) in the words of the 플랜 page; an id it does not know shows as it is.
    private static string PlanName(string plan) => plan switch
    {
        "premium" => "프로", "ultra" => "울트라", "biz" => "산업",
        _ when System.Text.RegularExpressions.Regex.IsMatch(plan, @"^((ultra|biz)_)?(monthly|yearly|lifetime)(_x\d+)?$") => PremiumPage.PlanLabel(plan),
        _ => plan,
    };

    /// The day the key was issued and the days since, counted like the server: whole 24-hour periods.
    private static string Since(DateTime issuedUtc)
    {
        var day = DateTime.SpecifyKind(issuedUtc, DateTimeKind.Utc).ToLocalTime().Date;
        var days = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - DateTime.SpecifyKind(issuedUtc, DateTimeKind.Utc)).TotalDays));
        return days == 0 ? $"{day:yyyy-MM-dd} · 하루가 지나지 않았어요" : $"{day:yyyy-MM-dd} · {days}일 지났어요";
    }

    private static Border StatusTag(string status, bool gone) => Ui.Tag(gone ? "서버에 없음" : status switch
    {
        "answered" => "답변 왔어요",
        "closed" => "닫힘",
        _ => "답변 기다리는 중",
    });

    private static string RefundStatus(string status) => status switch
    {
        "approved" => "환불 승인됨",
        "rejected" => "환불 거절됨",
        "done" => "환불 완료",
        _ => "환불 요청됨",
    };

    // ------------------------------------------------------------ page state
    private void ShowTab()
    {
        foreach (var (tab, b) in _tabButtons)
        {
            b.Classes.Set("primary", tab == _tab);
            b.Classes.Set("ghost", tab != _tab);
        }
        if (_tab == Tab.Mine && _thread != null) RenderThread();
        _body.Content = _tab switch
        {
            Tab.Ask => _askPanel,
            Tab.Refund => _refundPanel,
            Tab.Mine => _thread != null ? _threadView : ListPanel(),
            _ => FaqPanel(),
        };
    }

    private void UpdateBadge()
    {
        var n = Support.Unread;
        _mineBadge.IsVisible = n > 0;
        if (_mineBadge.Child is TextBlock t) t.Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnTicketsChanged()
    {
        UpdateBadge();
        if (_tab == Tab.Mine && _thread == null) _body.Content = ListPanel();
    }

    private void OnLicenseChanged()
    {
        ShowSubscription();
        ShowKeyParts();
    }

    private async Task LoadInfo()
    {
        _info = await Support.InfoAsync();
        _loaded = true;
        ShowBanner();
        FillCategories();
        FillReasons();
        ShowSeller();
        _askSend.IsEnabled = _info.Open && !_askSending;
        _rfSend.IsEnabled = _info.Open && !_rfSending;
        if (_tab == Tab.Faq) _body.Content = FaqPanel();
        else if (_tab == Tab.Mine) ShowTab();   // kind names may have come with the answer
    }

    private void ShowBanner()
    {
        var lines = new List<string>();
        if (!_info.Reachable)
            lines.Add("서버에 연결하지 못했어요. 문의를 보내거나 답을 보려면 인터넷 연결이 필요해요. 자주 묻는 질문은 앱에 들어 있는 기본 도움말을 보여 줘요.");
        else
        {
            if (!_info.Open) lines.Add("지금은 앱에서 문의를 받지 않아요. 판매 페이지에 있는 연락처로 문의해 주세요");
            if (_info.Notice.Trim().Length > 0) lines.Add(_info.Notice.Trim());
        }
        _banner.IsVisible = lines.Count > 0;
        _bannerText.Text = string.Join("\n", lines);
    }

    // ------------------------------------------------------------ 자주 묻는 질문
    private Control FaqPanel()
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(Ui.Text("자주 묻는 질문", "h2"));
        if (!_loaded)
        {
            p.Children.Add(Ui.Text("불러오는 중…", "muted"));
            return p;
        }
        if (_info.BuiltInFaq)
        {
            var mark = Ui.Text(_info.Reachable ? "앱에 들어 있는 기본 도움말이에요."
                                               : "오프라인 · 서버에 연결하지 못해서 앱에 들어 있는 짧은 기본 도움말을 보여 줘요.", "accent label", wrap: true);
            mark.Margin = new Thickness(0, 4, 0, 6);
            p.Children.Add(mark);
        }
        foreach (var f in _info.Faq) p.Children.Add(FaqItem(f));
        var more = Ui.Text("찾는 답이 없으면 '문의하기'로 보내 주세요.", "muted", wrap: true);
        more.Margin = new Thickness(0, 8, 0, 0);
        p.Children.Add(more);
        return p;
    }

    private static Control FaqItem(SupportFaq f)
    {
        var arrow = new TextBlock { Text = "▸", Margin = new Thickness(0, 0, 10, 0) };
        var question = new TextBlock { Text = f.Q, TextWrapping = TextWrapping.Wrap };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        head.Children.Add(arrow);
        Grid.SetColumn(question, 1);
        head.Children.Add(question);
        var b = new Button { Content = head };
        b.Classes.Add("nav");
        AutomationProperties.SetName(b, f.Q);
        var answer = Ui.Text(f.A, "label", wrap: true);
        answer.Margin = new Thickness(38, 0, 14, 12);
        answer.IsVisible = false;
        b.Click += (_, _) =>
        {
            answer.IsVisible = !answer.IsVisible;
            arrow.Text = answer.IsVisible ? "▾" : "▸";
            b.Classes.Set("on", answer.IsVisible);
        };
        var p = new StackPanel();
        p.Children.Add(b);
        p.Children.Add(answer);
        return p;
    }

    // ------------------------------------------------------------ 문의하기
    private void BuildAsk()
    {
        var p = _askPanel;
        p.Children.Add(Ui.Text("문의하기", "h2"));
        p.Children.Add(Ui.Text("환불을 원하면 '환불 요청'에서 보내 주세요. 키 정보가 함께 가서 판매자가 주문을 바로 찾을 수 있어요.", "muted", wrap: true));
        p.Children.Add(Ui.Text("문의 종류", "label"));
        var catBox = new Border { Child = _askCats, HorizontalAlignment = HorizontalAlignment.Left };
        catBox.Classes.Add("segbox");
        p.Children.Add(catBox);
        p.Children.Add(_askSubject);
        p.Children.Add(_askText);
        Count(_askText, _askCount);
        p.Children.Add(_askCount);
        p.Children.Add(_askContact);
        p.Children.Add(Ui.Text(ContactNote, "muted", wrap: true));
        p.Children.Add(_askDiag);
        p.Children.Add(_diagLines);
        _ = FillDiag();
        p.Children.Add(Consent(_askConsent));
        p.Children.Add(Left(_askSend));
        p.Children.Add(_askMsg);

        _askSend.Click += async (_, _) =>
        {
            if (_askConsent.IsChecked != true) { _askMsg.Text = "보관에 동의해야 보낼 수 있어요"; return; }
            _askSending = true;
            _askSend.IsEnabled = false;
            _askMsg.Text = "보내는 중…";
            var diag = _askDiag.IsChecked == true ? await Support.DiagAsync() : null;
            var (id, error) = await Support.CreateAsync(new SupportDraft(_askCat ?? "", _askSubject.Text ?? "", _askText.Text ?? "", _askContact.Text, diag));
            _askSending = false;
            _askSend.IsEnabled = _info.Open;
            if (id == null) { _askMsg.Text = error ?? ""; return; }
            _askSubject.Text = _askText.Text = _askContact.Text = "";
            _askMsg.Text = "";
            OpenThread(id, $"보냈어요. 문의 번호 {id}");
        };
    }

    /// The exact values the box would send, shown before it is ticked.
    private async Task FillDiag()
    {
        void Lines(SupportDiag? d)
        {
            _diagLines.Children.Clear();
            _diagLines.Children.Add(Ui.Text("앱 정보는 선택이에요. 체크한 경우에만 아래 값을 보내고 문의와 같은 기간 보관해요.", "muted", wrap: true));
            if (d == null) { _diagLines.Children.Add(Ui.Text("앱 정보를 확인하는 중…", "muted")); return; }
            _diagLines.Children.Add(Ui.Text($"앱 버전: {d.App}", "muted"));
            _diagLines.Children.Add(Ui.Text($"운영체제: {d.Os}", "muted"));
            _diagLines.Children.Add(Ui.Text(d.Gpu.Length > 0 ? $"GPU 이름: {d.Gpu}" : "GPU 이름: 확인하지 못해서 보내지 않아요", "muted", wrap: true));
        }
        Lines(null);
        Lines(await Support.DiagAsync());
    }

    private void FillCategories()
    {
        _askCats.Children.Clear();
        var cats = _info.Categories.Where(c => c.Id != "refund").ToList();   // refunds have their own tab
        if (_askCat != null && cats.All(c => c.Id != _askCat)) _askCat = null;
        foreach (var c in cats)
        {
            var t = new ToggleButton { Content = c.Label, IsChecked = c.Id == _askCat, Tag = c.Id };
            t.Classes.Add("seg");
            t.Click += (_, _) =>
            {
                _askCat = c.Id;
                foreach (var o in _askCats.Children.OfType<ToggleButton>()) o.IsChecked = (string?)o.Tag == c.Id;
            };
            _askCats.Children.Add(t);
        }
    }

    // ------------------------------------------------------------ 환불 요청
    private void BuildRefund()
    {
        var p = _refundPanel;
        p.Children.Add(Ui.Text("환불 요청", "h2"));
        p.Children.Add(Ui.Text("요청을 판매자에게 보내요. 판매자가 확인하고 환불 여부와 방법을 '내 문의'로 답해 줘요.", "muted", wrap: true));
        p.Children.Add(_rfFacts);
        p.Children.Add(_rfOrder);   // ShowKeyParts fills in the order still waiting for its key, if any
        p.Children.Add(Ui.Text("주문 코드는 '★ 플랜'에서 결제할 때 받은 SB-로 시작하는 코드예요. 이 PC에 키가 있으면 적지 않아도 되고, 적으면 키와 함께 보내요.", "muted", wrap: true));
        p.Children.Add(Clause9());
        p.Children.Add(_rfCard);
        p.Children.Add(Ui.Text("환불 사유", "label"));
        _rfReason.SelectionChanged += (_, _) =>
        {
            var i = _rfReason.SelectedIndex;
            if (i >= 0 && i < _info.RefundReasons.Count) _rfReasonId = _info.RefundReasons[i].Id;
        };
        p.Children.Add(_rfReason);
        p.Children.Add(_rfText);
        Count(_rfText, _rfCount);
        p.Children.Add(_rfCount);
        p.Children.Add(_rfContact);
        p.Children.Add(Ui.Text(ContactNote, "muted", wrap: true));
        p.Children.Add(Consent(_rfConsent));
        p.Children.Add(Left(_rfSend));
        p.Children.Add(_rfMsg);

        _rfSend.Click += async (_, _) =>
        {
            if (_rfConsent.IsChecked != true) { _rfMsg.Text = "보관에 동의해야 보낼 수 있어요"; return; }
            var reason = _info.RefundReasons.FirstOrDefault(r => r.Id == _rfReasonId);
            if (reason == null) { _rfMsg.Text = "환불 사유를 골라 주세요"; return; }
            var info = License.Info;
            var order = (_rfOrder.Text ?? "").Trim().ToUpperInvariant();
            var subject = Lang.T("환불 요청") + (info != null ? " · " + Lang.T(PlanName(info.Plan)) : order.Length > 0 ? " · " + order : "");
            var text = (_rfText.Text ?? "").Trim();
            if (text.Length == 0) text = Lang.T(reason.Label);
            _rfSending = true;
            _rfSend.IsEnabled = false;
            _rfMsg.Text = "보내는 중…";
            // the key itself goes along, so the server can check it is this buyer's; the server keeps only its ID
            var (id, error) = await Support.CreateAsync(new SupportDraft("refund", subject, text, _rfContact.Text, null, order,
                                                                         License.KeyText(), License.KeyId, reason.Id));
            _rfSending = false;
            _rfSend.IsEnabled = _info.Open;
            if (id == null) { _rfMsg.Text = error ?? ""; return; }
            _rfText.Text = "";
            _rfMsg.Text = "";
            OpenThread(id, $"환불 요청을 보냈어요. 문의 번호 {id}");
        };
    }

    private void FillReasons()
    {
        var keep = _rfReasonId;
        _rfReason.ItemsSource = _info.RefundReasons.Select(r => r.Label).ToList();
        var i = _info.RefundReasons.ToList().FindIndex(r => r.Id == keep);
        _rfReason.SelectedIndex = i;
        _rfReasonId = i >= 0 ? keep : null;
    }

    private string? _rfPrefill;   // the order code the page filled in itself

    /// The key facts of this PC, the card subscription note and the order code box: all follow the key.
    /// A registered key goes along with the request; an order still waiting for its key is filled in whenever there
    /// is one (with or without an older key), so the request names that payment too. A code typed by hand is sent too.
    private void ShowKeyParts()
    {
        var id = License.KeyId;
        var info = License.Info;
        var pending = License.Pending?.Code;
        if (_rfPrefill != null && _rfPrefill != pending)
        {   // the page's own fill-in, for an order no longer waiting: taken out unless the user changed it
            if ((_rfOrder.Text ?? "").Trim() == _rfPrefill) _rfOrder.Text = "";
            _rfPrefill = null;
        }
        if (pending != null && string.IsNullOrWhiteSpace(_rfOrder.Text))
        {
            _rfOrder.Text = pending;
            _rfPrefill = pending;
        }
        var p = new StackPanel { Spacing = 6 };
        if (id == null)
            p.Children.Add(Ui.Text("이 PC에 등록된 키가 없어요. 아래에 주문 코드를 적어 주세요.", "label", wrap: true));
        else
        {
            var rows = new List<(string, string)> { ("키 식별 번호", id) };
            if (info != null)
            {
                rows.Add(("플랜 종류", PlanName(info.Plan)));
                rows.Add(("키를 받은 날", info.Issued is DateTime issued ? Since(issued) : "키에 적혀 있지 않아요"));
                rows.Add(("사용 기간", PlanStatus.Period));
                rows.Add(("받은 방법", PlanStatus.How));
            }
            else rows.Add(("키 정보", "기간이 끝났거나 읽을 수 없는 키예요. 판매자가 서버 기록으로 확인해요."));
            p.Children.Add(Facts(rows));
            p.Children.Add(Ui.Text("키 식별 번호는 키 자체가 아니라, 판매자가 이 키를 찾을 때 쓰는 번호예요.", "muted", wrap: true));
        }
        _rfFacts.Content = Ui.Card(p, "tile");

        if (info?.Sub == null) { _rfCard.Content = null; _rfCard.IsVisible = false; return; }
        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(Ui.Text("카드 구독 키예요. 환불이 정해지면 판매자가 카드 결제로 돌려줘요. 다음 결제가 되지 않게 하려면 '구독 관리'에서 구독을 해지해 주세요.", "label", wrap: true));
        var msg = Message();
        card.Children.Add(Left(PortalButton(msg)));
        card.Children.Add(msg);
        _rfCard.Content = Ui.Card(card, "note");
        _rfCard.IsVisible = true;
    }

    private static Control Clause9()
    {
        var p = new StackPanel { Spacing = 6 };
        p.Children.Add(Ui.Text("사용권 계약에서 옮겨 온 내용이에요.", "muted"));
        try
        {
            var lines = Eula.Lines();
            var start = Array.FindIndex(lines, l => l.StartsWith("## ") && (l.Contains("제9조") || l.Contains("Article 9 ")));
            if (start < 0) throw new InvalidOperationException();
            var title = Ui.Text(lines[start][3..].Trim(), "label");
            title.FontWeight = FontWeight.Bold;
            p.Children.Add(title);
            for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("## "); i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("! ")) line = line[2..];
                if (line.Length > 0) p.Children.Add(Ui.Text(line, "label", wrap: true));
            }
        }
        catch (Exception)
        {
            p.Children.Add(Ui.Text("사용권 계약 제9조는 '설정'의 '사용권 계약 보기'에서 볼 수 있어요.", "label", wrap: true));
        }
        return Ui.Card(p, "note");
    }

    // ------------------------------------------------------------ 구독 관리 (a key with a card subscription only)
    private Button PortalButton(TextBlock msg)
    {
        var b = Ui.Button("구독 관리", "ghost");
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            msg.Text = "구독 관리 링크를 받는 중…";
            var (url, error) = await Support.SubscriptionPortalAsync();
            b.IsEnabled = true;
            if (url == null) { msg.Text = error ?? ""; return; }
            try
            {
                if (TopLevel.GetTopLevel(this) is { } top && await top.Launcher.LaunchUriAsync(new Uri(url)))
                {
                    msg.Text = "브라우저에서 구독 관리 페이지를 열었어요.";
                    return;
                }
            }
            catch (Exception) { }
            msg.Text = "브라우저를 열지 못했어요. 결제 메일의 구독 관리 링크를 써 주세요.";
        };
        return b;
    }

    private void ShowSubscription()
    {
        if (License.Info?.Sub == null) { _subscription.Content = null; _subscription.IsVisible = false; return; }
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("구독 관리", "h2"));
        p.Children.Add(Ui.Text("카드 구독은 카드 결제 서비스의 구독 관리 페이지에서 해지할 수 있어요. 누르면 이 키의 구독 관리 페이지가 브라우저에서 열려요.", "muted", wrap: true));
        var msg = Message();
        p.Children.Add(Left(PortalButton(msg)));
        p.Children.Add(msg);
        _subscription.Content = Ui.Card(p);
        _subscription.IsVisible = true;
    }

    // ------------------------------------------------------------ 판매자 정보
    private void ShowSeller()
    {
        var s = _info.Seller;
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("판매자 정보", "h2"));
        if (s.Empty) p.Children.Add(Ui.Text("판매 페이지 참고", "label"));
        else
        {
            static string Or(string v) => string.IsNullOrWhiteSpace(v) ? "판매 페이지 참고" : v.Trim();
            p.Children.Add(Facts(new[]
            {
                ("상호", Or(s.Name)), ("대표자", Or(s.Rep)), ("주소", Or(s.Address)), ("사업자등록번호", Or(s.BizNo)),
                ("통신판매업 신고번호", Or(s.MailOrderNo)), ("메일", Or(s.Email)), ("전화", Or(s.Phone)), ("문의 받는 시간", Or(s.Hours)),
            }));
        }
        _seller.Content = Ui.Card(p);
    }

    // ------------------------------------------------------------ 내 문의
    // the frame stays; only the rows are drawn again, so the 새로 고침 message survives a change
    private readonly StackPanel _listView = new();
    private readonly StackPanel _listRows = new();
    private readonly TextBlock _listMsg = Message();

    private void BuildList()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        var title = Ui.Text("내 문의", "h2");
        title.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(title);
        var refresh = Ui.Button("새로 고침", "ghost");
        refresh.Click += async (_, _) => await Refresh(refresh, _listMsg);
        Grid.SetColumn(refresh, 1);
        head.Children.Add(refresh);
        _listView.Children.Add(head);
        _listView.Children.Add(_listMsg);
        _listView.Children.Add(_listRows);
        var note = Ui.Text($"문의 목록은 이 PC에만 저장되고, 최근 {Support.MaxKept}개까지 남아요.", "muted", wrap: true);
        note.Margin = new Thickness(0, 10, 0, 0);
        _listView.Children.Add(note);
    }

    private Control ListPanel()
    {
        var p = _listRows;
        p.Children.Clear();
        var list = Support.Tickets;
        if (list.Count == 0) p.Children.Add(Ui.Text("이 PC에서 보낸 문의가 없어요.", "muted"));
        bool first = true;
        foreach (var t in list)
        {
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (t.Unread) right.Children.Add(Ui.Badge("새 답변"));
            right.Children.Add(StatusTag(t.Status, t.Gone));
            var open = Ui.Button(t.Gone ? "목록에서 지우기" : "보기", "ghost");
            var id = t.Id;
            open.Click += (_, _) => { if (t.Gone) Support.Forget(id); else OpenThread(id); };
            right.Children.Add(open);
            p.Children.Add(Ui.Row(t.Subject, $"{t.Id} · {CatLabel(t.Cat)} · {t.Updated.ToLocalTime():yyyy-MM-dd HH:mm}", right, first));
            first = false;
        }
        return _listView;
    }

    private void BuildThread()
    {
        var back = Ui.Button("← 목록으로", "ghost");
        back.Click += (_, _) => { _thread = null; _ticket = null; ShowTab(); };
        var refresh = Ui.Button("새로 고침", "ghost");
        refresh.Click += async (_, _) => await Refresh(refresh, _replyMsg);
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        top.Children.Add(back);
        top.Children.Add(refresh);
        _threadView.Children.Add(top);
        _threadView.Children.Add(_threadTop);
        _replyArea.Children.Add(_replyState);
        _replyArea.Children.Add(_replyBox);
        Count(_replyBox, _replyCount);
        _replyArea.Children.Add(_replyCount);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(_replySend);
        row.Children.Add(_closeTicket);
        _replyArea.Children.Add(row);
        _threadView.Children.Add(_replyArea);
        _threadView.Children.Add(_replyMsg);

        _replySend.Click += async (_, _) =>
        {
            if (_thread is not { } id) return;
            _replySend.IsEnabled = false;
            _replyMsg.Text = "보내는 중…";
            ++_shown;   // a read already on its way must not draw over the reply
            var (t, error) = await Support.ReplyAsync(id, _replyBox.Text ?? "");
            ++_shown;   // nor one that left while the reply was on its way
            _replySend.IsEnabled = true;
            if (t == null) { _replyMsg.Text = error ?? ""; return; }
            _replyBox.Text = "";
            _replyMsg.Text = "보냈어요.";
            if (_thread == id) { _ticket = t; _threadNote = null; RenderThread(); }
        };
        _closeTicket.Click += async (_, _) =>
        {
            if (_thread is not { } id) return;
            _closeTicket.IsEnabled = false;
            ++_shown;
            var (t, error) = await Support.CloseAsync(id);
            ++_shown;
            _closeTicket.IsEnabled = true;
            if (t == null) { _replyMsg.Text = error ?? ""; return; }
            _replyMsg.Text = "문의를 닫았어요. 닫은 뒤 30일 안에는 답장을 보내 다시 열 수 있어요.";
            if (_thread == id) { _ticket = t; RenderThread(); }
        };
    }

    private async void OpenThread(string id, string? note = null)
    {
        if (_thread != id) { _replyBox.Text = ""; _replyMsg.Text = ""; }
        _tab = Tab.Mine;
        _thread = id;
        _ticket = null;
        _threadNote = note;
        _threadError = null;
        ShowTab();
        await LoadThread();
    }

    private async Task LoadThread()
    {
        if (_thread is not { } id) return;
        var mine = ++_shown;
        var (t, error) = await Support.GetAsync(id, read: true);
        if (mine != _shown || _thread != id) return;
        if (t == null)
        {
            if (_threadError == error) return;
            _threadError = error;
        }
        else
        {
            // an answer older than the one on screen (a reply or a close landed first) changes nothing
            if (_ticket?.Id == t.Id && (t.Messages.Count < _ticket.Messages.Count || (t.Updated != default && t.Updated < _ticket.Updated))) return;
            // a check that found nothing new leaves the screen (and the reply being typed) alone
            var same = _ticket != null && _threadError == null && t.Messages.Count == _ticket.Messages.Count && t.Status == _ticket.Status
                       && t.Refund?.Status == _ticket.Refund?.Status;
            _ticket = t;
            _threadError = null;
            if (same) return;
        }
        if (_tab == Tab.Mine) RenderThread();
    }

    private void RenderThread()
    {
        if (_thread is not { } id) return;
        var entry = Support.Find(id);
        var t = _ticket?.Id == id ? _ticket : null;
        _threadTop.Children.Clear();
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        var subject = Ui.Text(t?.Subject is { Length: > 0 } s ? s : entry?.Subject ?? id, "h2", wrap: true);
        subject.Margin = new Thickness(0, 0, 10, 0);
        head.Children.Add(subject);
        head.Children.Add(StatusTag(t?.Status ?? entry?.Status ?? "open", entry?.Gone == true));
        _threadTop.Children.Add(head);
        _threadTop.Children.Add(Ui.Text($"문의 번호 {id} · {CatLabel(t?.Cat ?? entry?.Cat ?? "other")}", "muted"));
        if (_threadNote != null) _threadTop.Children.Add(Ui.Text(_threadNote, "accent label", wrap: true));
        if (_threadError != null) _threadTop.Children.Add(Ui.Text(_threadError, "label", wrap: true));
        if (t == null && _threadError == null) _threadTop.Children.Add(Ui.Text("불러오는 중…", "muted"));
        if (t?.Refund is { } refund) _threadTop.Children.Add(RefundState(refund));
        if (t != null)
        {
            foreach (var m in t.Messages) _threadTop.Children.Add(Bubble(m));
            if (t.Contact is { Length: > 0 } contact) _threadTop.Children.Add(Ui.Text($"적은 연락처: {contact}", "muted", wrap: true));
        }

        var full = t != null && t.Messages.Count >= Support.MaxMessages;
        _replyArea.IsVisible = t != null && entry?.Gone != true;
        _replyBox.IsVisible = _replyCount.IsVisible = _replySend.IsVisible = !full;
        _closeTicket.IsVisible = t?.Status != "closed";
        _replyState.Text = full ? $"한 문의에는 글을 {Support.MaxMessages}개까지 쓸 수 있어요. 새 문의를 남겨 주세요"
                         : t?.Status == "closed" ? "닫힌 문의예요. 닫은 뒤 30일 안에 답장을 보내면 다시 열려요." : "";
        _replyState.IsVisible = _replyState.Text.Length > 0;
    }

    private Control RefundState(SupportRefund r)
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(Ui.Text(RefundStatus(r.Status), "label"));
        if (r.Status is not ("approved" or "rejected" or "done")) p.Children.Add(Ui.Text("판매자가 확인하고 있어요.", "muted"));
        if (r.Reason.Length > 0) p.Children.Add(Ui.Text($"사유: {ReasonLabel(r.Reason)}", "muted"));
        // the plan name is translated on its own first: "{0}대 묶음" would otherwise win over "플랜: {0}"
        if (r.Plan is { Length: > 0 } plan) p.Children.Add(Ui.Text($"플랜: {Lang.T(PlanName(plan))}", "muted"));
        if (r.Issued is { } issued) p.Children.Add(Ui.Text($"키를 받은 날: {issued.ToLocalTime():yyyy-MM-dd}", "muted"));
        if (r.DaysSinceKey is int days) p.Children.Add(Ui.Text($"요청할 때까지 지난 날: {days}일", "muted"));
        if (r.Status == "rejected" && r.RejectReason is { Length: > 0 } why) p.Children.Add(Ui.Text($"판매자가 적은 이유: {why}", "label", wrap: true));
        return Ui.Card(p, "tile");
    }

    private static Control Bubble(SupportMessage m)
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(Ui.Text($"{(m.FromSeller ? "판매자" : "나")} · {m.At.ToLocalTime():yyyy-MM-dd HH:mm}", m.FromSeller ? "muted accent" : "muted"));
        p.Children.Add(new SelectableTextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        var b = Ui.Card(p, "note");
        b.Margin = m.FromSeller ? new Thickness(0, 0, 60, 0) : new Thickness(60, 0, 0, 0);
        return b;
    }
}
