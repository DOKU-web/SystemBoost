// Member account: log in (아이디 or email, then the 2단계 인증 code when it is on), sign up (email + link when the
// server can send mail, else 아이디 as before), find a password; once in, the member, 2단계 인증, the invite code
// and its reward, the password and leaving. Same on Windows and macOS. Key-only users never need any of it.
// Passwords, codes and tokens stay in the controls and in memory: nothing here writes or logs them, except the
// recovery codes the member saves or prints on purpose (AccountTwoFactor.cs).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using SystemBoost.App.Services;
using System;
using System.Threading.Tasks;

namespace SystemBoost.App.Pages;

public sealed partial class AccountPage : UserControl
{
    private enum Mode { LogIn, SignUp, Recover, Resend, Request }

    private readonly StackPanel _root = new();
    private Mode _mode = Mode.LogIn;
    private string? _recovery;      // 아이디 account recovery code: shown once, until the member says it is kept
    private string? _pending;       // 2단계 인증 login waiting for its code (5 minutes on the server); memory only
    private bool _pendingBackup;    // ... with a recovery code instead of the authenticator
    private string? _sentTo;        // email sign-up went out: "check your mail" until the member goes on
    private string? _carry;         // a message for the next form (e.g. the code step ran out of time)
    private (int? left, bool redo)? _backupLogin;   // just logged in with a recovery code: say what is left
    private int _shown;             // a slower reply for an older screen must not land on this one
    private readonly Action _openPlans;
    private readonly ContentControl _plan = new();
    private Task<AccountFeatures>? _featuresTask;   // asked again each time the page opens
    private AccountFeatures _features = AccountFeatures.None;

    public AccountPage(Action openPlans)
    {
        _openPlans = openPlans;
        Content = new ScrollViewer { Content = _root };
        AttachedToVisualTree += (_, _) =>
        {
            Account.Changed += Show;
            License.Changed += ShowPlan;
            _featuresTask = Account.FeaturesAsync();
            SweepPrintFiles();
            Show();
        };
        DetachedFromVisualTree += (_, _) => { Account.Changed -= Show; License.Changed -= ShowPlan; };
    }

    // ------------------------------------------------------------ plan in use (a key belongs to this device, member or not)
    private void ShowPlan() => _plan.Content = Ui.Card(PlanCard());

    private Control PlanCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("지금 플랜", "muted"));
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var name = Ui.Text(PlanStatus.Name, "h2");
        name.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF7A1A"));
        head.Children.Add(name);
        head.Children.Add(Ui.Badge("사용 중"));
        p.Children.Add(head);
        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        void Fact(int row, string title, string value)
        {
            var left = Ui.Text(title, "muted");
            var right = Ui.Text(value, "label", wrap: true);
            right.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetRow(left, row);
            Grid.SetRow(right, row);
            Grid.SetColumn(right, 1);
            facts.Children.Add(left);
            facts.Children.Add(right);
        }
        Fact(0, "사용 방식", PlanStatus.How);
        Fact(1, "사용 기간", PlanStatus.Period);
        Fact(2, "프레임 생성 배수", PlanStatus.Multipliers);
        p.Children.Add(facts);
        bool top = PlanStatus.Rank >= PlanStatus.TopRank && !PlanStatus.CanExtend;
        var open = Ui.Button(top ? "플랜 보기" : "플랜 업그레이드", top ? "ghost" : "primary");
        open.Click += (_, _) => _openPlans();
        p.Children.Add(Left(open));
        return p;
    }

    // ------------------------------------------------------------ small helpers
    private static TextBox Input(string hint, bool secret = false, int max = 72)
    {
        var box = new TextBox { Watermark = hint, MaxLength = max };
        if (secret) box.PasswordChar = '•';
        return box;
    }

    private static TextBlock Message() => Ui.Text("", "muted", wrap: true);

    private static Control Left(Control c)
    {
        c.HorizontalAlignment = HorizontalAlignment.Left;
        return c;
    }

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    private static TextBlock Heading(string text)
    {
        var t = Ui.Text(text, "h2");
        t.Margin = new Thickness(0, 6, 0, 0);
        return t;
    }

    /// Enter in the box does what the button does.
    private static void OnEnter(TextBox box, Action act) =>
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            act();
        };

    /// Runs a server call with its button held down, then shows the error, if any, and says whether all went well.
    private static async Task<bool> Run(Button button, TextBlock message, Func<Task<string?>> call)
    {
        button.IsEnabled = false;
        message.Text = "";
        var error = await call();
        button.IsEnabled = true;
        message.Text = error ?? "";
        return error == null;
    }

    private async Task<AccountFeatures> Features() => _featuresTask is { } t ? await t : AccountFeatures.None;

    private async void Show()
    {
        var mine = ++_shown;
        _root.Children.Clear();
        _root.Children.Add(Ui.Header("계정", "회원이 아니어도 앱은 그대로 쓸 수 있어요. 회원은 친구를 초대하고 보상을 받을 수 있어요."));
        if (_recovery != null) { _root.Children.Add(Ui.Card(RecoveryCard())); return; }
        if (_backup != null) { _root.Children.Add(Ui.Card(BackupCard())); return; }
        ShowPlan();
        _root.Children.Add(_plan);
        var wait = Ui.Text("불러오는 중…", "muted");
        _root.Children.Add(wait);
        if (!Account.SignedIn)
        {
            _setup = null;
            _features = await Features();
            if (mine != _shown) return;
            _root.Children.Remove(wait);
            _root.Children.Add(Ui.Card(_pending != null ? CodeStep() : _sentTo != null ? SentCard() : SignedOut()));
            return;
        }
        _mode = Mode.LogIn;   // the next time signed out starts at the login form
        _pending = null;
        _sentTo = null;
        var asked = Account.ProfileAsync();
        _features = await Features();
        var (profile, error) = await asked;
        if (mine != _shown) return;
        _root.Children.Remove(wait);
        if (!Account.SignedIn) return;   // the session ended; Changed shows the login card
        if (profile == null)
        {
            _root.Children.Add(Ui.Card(Ui.Text(error ?? "", "label", wrap: true)));
            _root.Children.Add(Ui.Card(LogOut()));
            return;
        }
        if (_backupLogin is { } used) _root.Children.Add(Ui.Card(BackupLoginCard(used.left, used.redo), "note"));
        _root.Children.Add(Ui.Card(Member(profile)));
        if (_features.Totp)   // accounts v2 servers only
        {
            _root.Children.Add(Ui.Card(Keys()));
            _root.Children.Add(Ui.Card(LinkKey()));
            _root.Children.Add(Ui.Card(Devices()));
            _root.Children.Add(Ui.Card(TwoFactor(profile)));
        }
        _root.Children.Add(Ui.Card(Invite(profile)));
        _root.Children.Add(Ui.Card(Password(profile)));
        _root.Children.Add(Ui.Card(Leave()));
    }

    // ------------------------------------------------------------ signed out
    private Control SignedOut()
    {
        var p = new StackPanel { Spacing = 10 };
        var tabs = new WrapPanel { Orientation = Orientation.Horizontal };
        void Tab(string text, Mode mode)
        {
            var b = Ui.Button(text, _mode == mode ? "primary" : "ghost");
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Click += (_, _) => { _mode = mode; _carry = null; Show(); };
            tabs.Children.Add(b);
        }
        Tab("로그인", Mode.LogIn);
        Tab("회원 가입", Mode.SignUp);
        Tab("비밀번호 찾기", Mode.Recover);
        Tab("키 다시 받기", Mode.Resend);
        Tab("계정 복구 요청", Mode.Request);
        p.Children.Add(tabs);
        p.Children.Add(_mode switch
        {
            Mode.SignUp => _features.Email ? SignUpEmail() : SignUp(),
            Mode.Recover => Recover(),
            Mode.Resend => Resend(),
            Mode.Request => Request(),
            _ => LogIn(),
        });
        return p;
    }

    private Control LogIn()
    {
        var p = new StackPanel { Spacing = 10 };
        // Email accounts exist only on v2 servers; older ones know the 아이디 alone.
        var login = Input(_features.Totp ? "아이디 또는 이메일" : "아이디", max: AccountRules.MaxEmail);
        var password = Input("비밀번호", secret: true);
        var go = Ui.Button("로그인", "primary");
        var msg = Message();
        msg.Text = _carry ?? "";
        _carry = null;
        async void Go()
        {
            if (!go.IsEnabled) return;
            var text = login.Text ?? "";
            if (AccountRules.IsEmail(text) && !AccountRules.ValidEmail(text)) { msg.Text = "이메일 주소를 확인해 주세요"; return; }
            LoginReply? reply = null;
            await Run(go, msg, async () =>
            {
                var (error, r) = await Account.LogInAsync(text, password.Text ?? "");
                reply = r;
                return error;
            });
            if (reply is { NeedsCode: true }) { _pending = reply.Pending; _pendingBackup = false; Show(); }
        }
        go.Click += (_, _) => Go();
        OnEnter(password, Go);
        p.Children.Add(login);
        p.Children.Add(password);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }

    /// The second step of a login with 2단계 인증 on: the authenticator's code, or one recovery code.
    private Control CodeStep()
    {
        var p = new StackPanel { Spacing = 10 };
        bool backup = _pendingBackup;
        p.Children.Add(Ui.Text("2단계 인증", "h2"));
        p.Children.Add(Ui.Text(backup ? "2단계 인증을 켤 때 받은 복구 코드 하나를 넣어 주세요. 쓴 코드는 다시 쓸 수 없어요."
                                      : "휴대폰 인증 앱에 보이는 6자리 코드를 넣어 주세요.", "muted", wrap: true));
        var code = Input(backup ? "복구 코드 (XXXX-XXXX-XXXX)" : "6자리 코드", max: backup ? 24 : 8);
        var go = Ui.Button("확인", "primary");
        var msg = Message();
        async void Go()
        {
            if (!go.IsEnabled || _pending is not { } pending) return;
            var value = backup ? AccountRules.BackupCode(code.Text) : AccountRules.TotpCode(code.Text);
            if (value == null)
            {
                msg.Text = backup ? "복구 코드는 XXXX-XXXX-XXXX 모양의 12자예요" : "인증 앱의 6자리 숫자를 넣어 주세요";
                return;
            }
            LoginReply? reply = null;
            bool again = false;
            string? error = null;
            await Run(go, msg, async () =>
            {
                (error, reply, again) = await Account.LogIn2Async(pending, value, backup);
                return error;
            });
            if (reply != null)
            {
                _pending = null;
                _backupLogin = backup ? (reply.BackupLeft, reply.Redo2fa) : null;
                Show();
            }
            else if (again)
            {
                _pending = null;
                _carry = error;
                Show();
            }
        }
        go.Click += (_, _) => Go();
        OnEnter(code, Go);
        var other = Ui.Button(backup ? "인증 앱 코드로 로그인" : "복구 코드로 로그인", "ghost");
        other.Click += (_, _) => { _pendingBackup = !backup; Show(); };
        var cancel = Ui.Button("취소", "ghost");
        cancel.Click += (_, _) => { _pending = null; Show(); };
        p.Children.Add(code);
        p.Children.Add(Row(go, other, cancel));
        p.Children.Add(msg);
        p.Children.Add(Ui.Text("휴대폰을 잃었다면 '복구 코드로 로그인'을 눌러 주세요. 복구 코드도 잃었다면 '계정 복구 요청'을 보내 주세요.", "muted", wrap: true));
        var request = Ui.Button("계정 복구 요청", "ghost");
        request.Click += (_, _) => { _pending = null; _mode = Mode.Request; Show(); };
        p.Children.Add(Left(request));
        return p;
    }

    private Control BackupLoginCard(int? left, bool redo)
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Ui.Text("복구 코드로 로그인했어요", "h2 accent"));
        if (left is int n)
            p.Children.Add(Ui.Text(n <= AccountRules.BackupFew ? $"남은 복구 코드는 {n}개예요. 아래 '2단계 인증'에서 새로 만들어 두세요."
                                                                : $"남은 복구 코드는 {n}개예요.", "label", wrap: true));
        p.Children.Add(Ui.Text("휴대폰을 바꿨거나 인증 앱을 잃었다면 아래 '2단계 인증'에서 끄고 다시 켜서 새 휴대폰에 등록해 주세요. 끌 때 복구 코드를 하나 더 써요.", "label", wrap: true));
        if (redo) p.Children.Add(Ui.Text("계정에 이메일이 있으면 복구 코드를 썼다는 알림 메일이 가요.", "muted", wrap: true));
        var ok = Ui.Button("확인", "ghost");
        ok.Click += (_, _) => { _backupLogin = null; Show(); };
        p.Children.Add(Left(ok));
        return p;
    }

    // What accounts.sql and the worker keep (privacy policy 제1조·제2조), the same for both kinds of sign-up.
    // Changing the meaning: raise AccountRules.ConsentVersion (sent with the sign-up) and the server's CONSENT.
    private static readonly string[] ConsentLines =
    {
        "수집 항목: 이메일 또는 아이디, 비밀번호(해시값으로만 저장), 아이디 계정의 복구 코드와 2단계 인증 복구 코드(해시값으로만 저장), 2단계 인증 비밀 값(암호화해서 저장), " +
            "가입과 마지막 로그인 시각, 동의한 안내문의 판과 시각, 초대 코드와 초대 관계, 초대 수와 보상 일수, 할인 티켓(할인율, 만료일, 티켓을 쓴 주문 코드와 시각), " +
            "로그인 실패 횟수와 잠금, 차단 여부, 로그인 세션과 기기 정보(기기 종류 이름, 마지막 접속 시각, 대략적인 국가), 계정에 연결한 키.",
        "보안 기록: 로그인, 비밀번호 변경, 2단계 인증 변경 같은 일의 시각, IP 주소, 국가, 기기 이름을 30일 동안 보관해요. 남용을 막으려고 IP 주소로 만든 해시값을 1시간 남짓 두고, " +
            "초대 코드를 쓰면 이 PC의 무작위 기기 번호를 90일 보관해요. 할인 티켓을 쓰면 그 주문(입금자명과 결제 메모가 들어 있어요)이 계정과 연결되고, 로그인한 채로 고객센터에 문의하면 문의에 아이디가 붙어요.",
        "목적: 회원 관리, 로그인과 계정 보안, 초대 보상, 할인 티켓, 키 찾기. 보관: 탈퇴할 때까지(백업에는 그 뒤 최대 7일), 보안 기록은 30일. 아이디는 초대 보상 키의 기록, 판매자 작업 기록(180일), 문의(그 문의의 보관 기간)에 남아요.",
        "회원 정보는 Supabase에, 판매자 서버는 Cloudflare(미국 회사, 전 세계 데이터센터)에 있어서 개인정보가 국외로 이전돼요. 메일은 메일 발송 서비스를 거쳐 보내요. 데이터베이스 위치와 업체는 개인정보 처리방침에 적혀 있어요.",
        "이름은 받지 않아요. 이메일은 선택이고, 로그인과 비밀번호 재설정, 보안 알림(새 기기 로그인, 비밀번호 변경 등)에만 써요. 광고 메일은 보내지 않아요. 동의하지 않으면 가입할 수 없지만, 앱은 회원이 아니어도 쓸 수 있어요.",
    };

    private static void AddConsent(StackPanel p, CheckBox consent, CheckBox age)
    {
        foreach (var line in ConsentLines) p.Children.Add(Ui.Text(line, "muted", wrap: true));
        var privacyStatus = Ui.Text("", "muted", wrap: true);
        privacyStatus.IsVisible = false;
        p.Children.Add(Left(Ui.PrivacyButton(privacyStatus)));
        p.Children.Add(privacyStatus);
        p.Children.Add(consent);
        p.Children.Add(age);
    }

    private static (CheckBox consent, CheckBox age) ConsentBoxes() =>
        (new CheckBox { Content = "개인정보 수집·이용에 동의합니다 (필수)" }, new CheckBox { Content = "만 14세 이상입니다 (필수)" });

    /// 아이디 sign-up, for servers that cannot send mail: as before, with a recovery code shown once.
    private Control SignUp()
    {
        var p = new StackPanel { Spacing = 10 };
        var id = Input("아이디 (영문 소문자, 숫자, 밑줄 4~20자)", max: 20);
        var password = Input($"비밀번호 ({Account.MinPassword}자 이상)", secret: true);
        var again = Input("비밀번호 다시 입력", secret: true);
        var invite = Input("초대 코드 (있으면)", max: 16);
        var (consent, age) = ConsentBoxes();
        var go = Ui.Button("가입하기", "primary");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            if (AccountRules.NewPasswordProblem(password.Text, again.Text, id.Text) is { } problem) { msg.Text = problem; return; }
            if (consent.IsChecked != true || age.IsChecked != true) { msg.Text = "개인정보 수집·이용 동의와 만 14세 이상 확인이 필요해요"; return; }
            string? recovery = null;
            await Run(go, msg, async () =>
            {
                var (error, code) = await Account.SignUpAsync(id.Text ?? "", password.Text ?? "", invite.Text ?? "");
                recovery = code;
                return error;
            });
            if (recovery != null) { _recovery = recovery; Show(); }
        };
        p.Children.Add(id);
        p.Children.Add(password);
        p.Children.Add(again);
        p.Children.Add(invite);
        AddConsent(p, consent, age);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }

    /// Email sign-up: the account is made when the link in the mail is pressed, then the member logs in here.
    private Control SignUpEmail()
    {
        var p = new StackPanel { Spacing = 10 };
        var email = Input("이메일", max: AccountRules.MaxEmail);
        var password = Input($"비밀번호 ({Account.MinPassword}자 이상)", secret: true);
        var again = Input("비밀번호 다시 입력", secret: true);
        var invite = Input("초대 코드 (있으면)", max: 16);
        var (consent, age) = ConsentBoxes();
        var go = Ui.Button("가입하기", "primary");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            var mail = email.Text ?? "";
            if (!AccountRules.ValidEmail(mail)) { msg.Text = "이메일 주소를 확인해 주세요"; return; }
            if (AccountRules.NewPasswordProblem(password.Text, again.Text, mail) is { } problem) { msg.Text = problem; return; }
            if (consent.IsChecked != true || age.IsChecked != true) { msg.Text = "개인정보 수집·이용 동의와 만 14세 이상 확인이 필요해요"; return; }
            if (await Run(go, msg, () => Account.SignUpEmailAsync(mail, password.Text ?? "", invite.Text ?? "")))
            {
                _sentTo = AccountRules.CleanEmail(mail);
                Show();
            }
        };
        p.Children.Add(Ui.Text("이메일과 비밀번호로 가입해요. 보낸 메일의 링크를 눌러야 가입이 끝나요.", "muted", wrap: true));
        p.Children.Add(email);
        p.Children.Add(password);
        p.Children.Add(again);
        p.Children.Add(invite);
        AddConsent(p, consent, age);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }

    private Control SentCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("인증 메일을 보냈어요", "h2 accent"));
        p.Children.Add(Ui.Text($"{_sentTo}(으)로 보낸 메일의 링크를 눌러 가입을 마친 뒤, 여기서 로그인해 주세요.", "label", wrap: true));
        p.Children.Add(Ui.Text("링크는 24시간 동안 쓸 수 있어요. 메일이 보이지 않으면 스팸함을 확인해 주세요. 이미 가입한 이메일이면 그 사실을 알리는 메일이 대신 가요.", "muted", wrap: true));
        var go = Ui.Button("로그인하기", "primary");
        go.Click += (_, _) => { _sentTo = null; _mode = Mode.LogIn; Show(); };
        p.Children.Add(Left(go));
        return p;
    }

    private Control Recover()
    {
        var p = new StackPanel { Spacing = 10 };
        if (_features.Email)
        {
            p.Children.Add(Heading("이메일로 찾기"));
            p.Children.Add(Ui.Text("가입한 이메일을 넣으면 비밀번호를 다시 정하는 링크를 보내요. 링크는 30분 동안 한 번만 쓸 수 있고, 비밀번호를 바꾸면 모든 기기에서 로그아웃돼요.", "muted", wrap: true));
            var email = Input("이메일", max: AccountRules.MaxEmail);
            var send = Ui.Button("재설정 메일 보내기", "primary");
            var sent = Message();
            send.Click += async (_, _) =>
            {
                var mail = email.Text ?? "";
                if (!AccountRules.ValidEmail(mail)) { sent.Text = "이메일 주소를 확인해 주세요"; return; }
                // The same words whether or not the address is a member's: nobody learns who signed up.
                if (await Run(send, sent, () => Account.ForgotAsync(mail))) sent.Text = "가입된 이메일이면 재설정 메일을 보냈어요.";
            };
            p.Children.Add(email);
            p.Children.Add(Left(send));
            p.Children.Add(sent);
            p.Children.Add(Heading("아이디로 찾기"));
        }
        var id = Input("아이디", max: 20);
        var code = Input("복구 코드", max: 32);
        var next = Input($"새 비밀번호 ({Account.MinPassword}자 이상)", secret: true);
        var go = Ui.Button("비밀번호 바꾸기", "primary");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            if (AccountRules.NewPasswordProblem(next.Text, null, id.Text) is { } problem) { msg.Text = problem; return; }
            string? recovery = null;
            await Run(go, msg, async () =>
            {
                var (error, fresh) = await Account.RecoverAsync(id.Text ?? "", code.Text ?? "", next.Text ?? "");
                recovery = fresh;
                return error;
            });
            if (recovery != null) { _recovery = recovery; Show(); }
        };
        p.Children.Add(Ui.Text(_features.Totp ? "아이디로 가입할 때 받은 복구 코드(XXXX-XXXX-XXXX-XXXX)로 비밀번호를 바꿔요. 복구 코드도 잃었다면 '계정 복구 요청'을 보내 주세요."
                                              : "아이디로 가입할 때 받은 복구 코드(XXXX-XXXX-XXXX-XXXX)로 비밀번호를 바꿔요. 복구 코드도 잃었다면 판매자에게 문의해 주세요.", "muted", wrap: true));
        p.Children.Add(id);
        p.Children.Add(code);
        p.Children.Add(next);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }

    private Control RecoveryCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("복구 코드를 보관하세요", "h2 accent"));
        p.Children.Add(Ui.Text("비밀번호를 잊으면 이 코드로 바꿀 수 있어요. 이 화면을 닫으면 다시 볼 수 없어요. 이메일을 추가하지 않았다면 이 코드가 비밀번호를 찾는 유일한 방법이니 꼭 적어 두세요.", "label", wrap: true));
        p.Children.Add(new SelectableTextBlock { Text = _recovery, FontSize = 20 });
        var copy = Ui.Button("복사", "ghost");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } board) await board.SetTextAsync(_recovery);
        };
        var done = Ui.Button("보관했어요", "primary");
        done.Click += (_, _) => { _recovery = null; Show(); };
        p.Children.Add(Row(copy, done));
        return p;
    }

    // ------------------------------------------------------------ signed in
    /// The email shown instead of the id: email accounts have an internal id ("m" + 11 letters and digits).
    private static string Shown(AccountProfile m) => m.Extras?.Email is { } mail && m.Extras.EmailVerified ? mail : m.Id;

    private static Control LogOut()
    {
        var go = Ui.Button("로그아웃", "ghost");
        go.Click += async (_, _) => { go.IsEnabled = false; await Account.LogOutAsync(); };
        return Left(go);
    }

    private Control Invite(AccountProfile m)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("친구 초대", "h2"));
        p.Children.Add(Ui.Text($"내 초대 코드로 가입한 사람은 프로 {m.InviteDays}일과 {m.InviteTicket}% 할인 티켓을 받고, 나는 한 명당 {m.RewardEach}일을 받아요. 받을 수 있는 보상은 모두 {m.RewardCap}일까지예요.", "muted", wrap: true));
        p.Children.Add(Ui.Text("할인 티켓은 판매자가 계좌이체 · 직접 결제를 열어 둔 플랜에서만 쓸 수 있어요. 카드 결제와 묶음 키에는 쓸 수 없어요.", "muted", wrap: true));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (m.Code != null) row.Children.Add(new SelectableTextBlock { Text = m.Code, FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        var msg = Message();
        if (m.Code != null)
        {
            var copy = Ui.Button("복사", "ghost");
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } board) await board.SetTextAsync(m.Code);
                msg.Text = "복사했어요";
            };
            row.Children.Add(copy);
        }
        var make = Ui.Button(m.Code == null ? "초대 코드 만들기" : "새 코드로 바꾸기", m.Code == null ? "primary" : "ghost");
        make.Click += async (_, _) => { if (await Run(make, msg, Account.NewInviteCodeAsync)) Show(); };
        row.Children.Add(make);
        p.Children.Add(row);
        if (m.Code != null) p.Children.Add(Ui.Text("코드를 바꾸면 지금 코드는 쓸 수 없게 돼요. 초대한 기록과 보상은 남아요.", "muted", wrap: true));
        p.Children.Add(Ui.Text($"초대한 사람 {m.Invites}명 · 쌓인 보상 {m.EarnedDays}일 / {m.RewardCap}일", "label"));
        if (m.RewardDays > 0)
        {
            var claim = Ui.Button($"보상 {m.RewardDays}일 받기", "primary");
            claim.Click += async (_, _) => { if (await Run(claim, msg, Account.ClaimRewardAsync)) Show(); };
            p.Children.Add(Left(claim));
        }
        p.Children.Add(msg);
        return p;
    }

    private Control Password(AccountProfile m)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("비밀번호 바꾸기", "h2"));
        var now = Input("지금 비밀번호", secret: true);
        var next = Input($"새 비밀번호 ({Account.MinPassword}자 이상)", secret: true);
        var again = Input("비밀번호 다시 입력", secret: true);
        var go = Ui.Button("바꾸기", "ghost");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            if (AccountRules.NewPasswordProblem(next.Text, again.Text, m.Id, m.Extras?.Email) is { } problem) { msg.Text = problem; return; }
            string? recovery = null;
            bool done = await Run(go, msg, async () =>
            {
                var (error, fresh) = await Account.ChangePasswordAsync(now.Text ?? "", next.Text ?? "");
                recovery = fresh;
                return error;
            });
            if (recovery != null) { _recovery = recovery; Show(); }
            else if (done) msg.Text = "비밀번호를 바꿨어요";
        };
        p.Children.Add(now);
        p.Children.Add(next);
        p.Children.Add(again);
        p.Children.Add(Left(go));
        p.Children.Add(Ui.Text(m.Extras?.Email != null ? "비밀번호를 바꾸면 다른 기기에서는 로그아웃되고, 이메일로 알림이 가요."
                                                       : "비밀번호를 바꾸면 복구 코드도 바뀌고, 다른 기기에서는 로그아웃돼요.", "muted", wrap: true));
        p.Children.Add(msg);
        return p;
    }

    private static Control Leave()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("회원 탈퇴", "h2"));
        p.Children.Add(Ui.Text("아이디와 이메일, 초대 기록, 받지 않은 보상, 쓰지 않은 할인 티켓, 로그인 기기, 2단계 인증과 복구 코드, 키 연결 기록을 회원 데이터베이스에서 바로 지우고 되돌릴 수 없어요. " +
                               "서비스 회사의 백업에는 최대 7일 남았다가 지워져요. 아이디는 초대 보상 키의 기록, 판매자 작업 기록(180일), 문의에 남아요. " +
                               "결제 기록은 법에서 정한 기간 동안 따로 보관해요. 키는 계정 없이도 키만으로 기간까지 계속 쓸 수 있어요.", "muted", wrap: true));
        var password = Input("비밀번호", secret: true);
        var go = Ui.Button("탈퇴하기", "ghost");
        var msg = Message();
        go.Click += async (_, _) => await Run(go, msg, () => Account.DeleteAsync(password.Text ?? ""));
        p.Children.Add(password);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }
}
