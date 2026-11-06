// Member account: log in, sign up, recover a password; once in, the invite code, its reward, the password
// and leaving. Same on Windows and macOS.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SystemBoost.App.Services;
using System;
using System.Threading.Tasks;

namespace SystemBoost.App.Pages;

public sealed class AccountPage : UserControl
{
    private enum Mode { LogIn, SignUp, Recover }

    private readonly StackPanel _root = new();
    private Mode _mode = Mode.LogIn;
    private string? _recovery;   // shown once, until the member says it is kept
    private int _shown;          // a slower reply for an older screen must not land on this one

    public AccountPage()
    {
        Content = new ScrollViewer { Content = _root };
        AttachedToVisualTree += (_, _) => { Account.Changed += Show; Show(); };
        DetachedFromVisualTree += (_, _) => Account.Changed -= Show;
    }

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

    private async void Show()
    {
        var mine = ++_shown;
        _root.Children.Clear();
        _root.Children.Add(Ui.Header("계정", "회원이 아니어도 앱은 그대로 쓸 수 있어요. 회원은 친구를 초대하고 보상을 받을 수 있어요."));
        if (_recovery != null) { _root.Children.Add(Ui.Card(RecoveryCard())); return; }
        if (!Account.SignedIn) { _root.Children.Add(Ui.Card(SignedOut())); return; }
        _mode = Mode.LogIn;   // the next time signed out starts at the login form
        var wait = Ui.Text("불러오는 중…", "muted");
        _root.Children.Add(wait);
        var (profile, error) = await Account.ProfileAsync();
        if (mine != _shown) return;
        _root.Children.Remove(wait);
        if (!Account.SignedIn) return;   // the session ended; Changed shows the login card
        if (profile == null)
        {
            _root.Children.Add(Ui.Card(Ui.Text(error ?? "", "label", wrap: true)));
            _root.Children.Add(Ui.Card(LogOut()));
            return;
        }
        _root.Children.Add(Ui.Card(Member(profile)));
        _root.Children.Add(Ui.Card(Invite(profile)));
        _root.Children.Add(Ui.Card(Password()));
        _root.Children.Add(Ui.Card(Leave()));
    }

    // ------------------------------------------------------------ signed out
    private Control SignedOut()
    {
        var p = new StackPanel { Spacing = 10 };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Tab(string text, Mode mode)
        {
            var b = Ui.Button(text, _mode == mode ? "primary" : "ghost");
            b.Click += (_, _) => { _mode = mode; Show(); };
            tabs.Children.Add(b);
        }
        Tab("로그인", Mode.LogIn);
        Tab("회원 가입", Mode.SignUp);
        Tab("비밀번호 찾기", Mode.Recover);
        p.Children.Add(tabs);
        p.Children.Add(_mode switch { Mode.SignUp => SignUp(), Mode.Recover => Recover(), _ => LogIn() });
        return p;
    }

    private static Control LogIn()
    {
        var p = new StackPanel { Spacing = 10 };
        var id = Input("아이디", max: 20);
        var password = Input("비밀번호", secret: true);
        var go = Ui.Button("로그인", "primary");
        var msg = Message();
        go.Click += async (_, _) => await Run(go, msg, () => Account.LogInAsync(id.Text ?? "", password.Text ?? ""));
        p.Children.Add(id);
        p.Children.Add(password);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }

    private Control SignUp()
    {
        var p = new StackPanel { Spacing = 10 };
        var id = Input("아이디 (영문 소문자, 숫자, 밑줄 4~20자)", max: 20);
        var password = Input($"비밀번호 ({Account.MinPassword}자 이상)", secret: true);
        var again = Input("비밀번호 다시 입력", secret: true);
        var invite = Input("초대 코드 (있으면)", max: 16);
        var consent = new CheckBox { Content = "개인정보 수집·이용에 동의합니다 (필수)" };
        var age = new CheckBox { Content = "만 14세 이상입니다 (필수)" };
        var go = Ui.Button("가입하기", "primary");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            if (password.Text != again.Text) { msg.Text = "비밀번호가 서로 달라요"; return; }
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
        p.Children.Add(Ui.Text("수집 항목: 아이디, 비밀번호(암호화해서 저장), 가입·로그인 시각, 초대 코드와 초대 관계. 목적: 회원 관리와 초대 혜택. 보관: Supabase 데이터베이스에 탈퇴할 때까지. " +
                               "메일이나 이름은 받지 않아요. 동의하지 않으면 가입할 수 없지만, 앱은 회원이 아니어도 쓸 수 있어요.", "muted", wrap: true));
        p.Children.Add(consent);
        p.Children.Add(age);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }

    private Control Recover()
    {
        var p = new StackPanel { Spacing = 10 };
        var id = Input("아이디", max: 20);
        var code = Input("복구 코드", max: 32);
        var next = Input("새 비밀번호", secret: true);
        var go = Ui.Button("비밀번호 바꾸기", "primary");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            string? recovery = null;
            await Run(go, msg, async () =>
            {
                var (error, fresh) = await Account.RecoverAsync(id.Text ?? "", code.Text ?? "", next.Text ?? "");
                recovery = fresh;
                return error;
            });
            if (recovery != null) { _recovery = recovery; Show(); }
        };
        p.Children.Add(Ui.Text("가입할 때 받은 복구 코드로 비밀번호를 바꿔요. 복구 코드도 잃었다면 판매자에게 문의해 주세요.", "muted", wrap: true));
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
        p.Children.Add(Ui.Text("비밀번호를 잊으면 이 코드로 바꿀 수 있어요. 이 화면을 닫으면 다시 볼 수 없어요. 메일로 찾을 수 없으니 꼭 적어 두세요.", "label", wrap: true));
        p.Children.Add(new SelectableTextBlock { Text = _recovery, FontSize = 20 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var copy = Ui.Button("복사", "ghost");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } board) await board.SetTextAsync(_recovery);
        };
        var done = Ui.Button("보관했어요", "primary");
        done.Click += (_, _) => { _recovery = null; Show(); };
        row.Children.Add(copy);
        row.Children.Add(done);
        p.Children.Add(row);
        return p;
    }

    // ------------------------------------------------------------ signed in
    private static Control Member(AccountProfile m)
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text(m.Id, "h2"));
        p.Children.Add(Ui.Text($"가입일 {m.Created.LocalDateTime:yyyy-MM-dd}", "muted"));
        p.Children.Add(LogOut());
        return p;
    }

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
        p.Children.Add(Ui.Text($"내 초대 코드로 가입한 사람은 프리미엄 {m.InviteDays}일을 받고, 나는 한 명당 {m.RewardEach}일을 받아요. 받을 수 있는 보상은 모두 {m.RewardCap}일까지예요.", "muted", wrap: true));
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

    private Control Password()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("비밀번호 바꾸기", "h2"));
        var now = Input("지금 비밀번호", secret: true);
        var next = Input($"새 비밀번호 ({Account.MinPassword}자 이상)", secret: true);
        var go = Ui.Button("바꾸기", "ghost");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            string? recovery = null;
            await Run(go, msg, async () =>
            {
                var (error, fresh) = await Account.ChangePasswordAsync(now.Text ?? "", next.Text ?? "");
                recovery = fresh;
                return error;
            });
            if (recovery != null) { _recovery = recovery; Show(); }
        };
        p.Children.Add(now);
        p.Children.Add(next);
        p.Children.Add(Left(go));
        p.Children.Add(Ui.Text("비밀번호를 바꾸면 복구 코드도 바뀌고, 다른 기기에서는 로그아웃돼요.", "muted", wrap: true));
        p.Children.Add(msg);
        return p;
    }

    private static Control Leave()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("회원 탈퇴", "h2"));
        p.Children.Add(Ui.Text("아이디와 초대 기록, 받지 않은 보상을 바로 지우고 되돌릴 수 없어요. 이미 받은 키는 기간까지 쓸 수 있어요.", "muted", wrap: true));
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
