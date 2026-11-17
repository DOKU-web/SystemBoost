// Account page, signed in, accounts v2 servers: the member's email, 내 라이선스 (keys linked to the account),
// 가진 키 연결하기 and 로그인된 기기. Lists load after the page shows; a reply for an older screen is dropped.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SystemBoost.App.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SystemBoost.App.Pages;

public sealed partial class AccountPage
{
    /// Email accounts have an internal id ("m" + 11 letters and digits) that is never a login name.
    private static bool InternalId(string id) => Regex.IsMatch(id, "^m[a-z0-9]{11}$");

    private Control Member(AccountProfile m)
    {
        var x = m.Extras ?? new AccountExtras(null, false, false, null);
        var p = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text(Shown(m), "h2"));
        if (x.Email != null && x.EmailVerified) head.Children.Add(Ui.Badge("이메일 확인됨"));
        p.Children.Add(head);
        if (Shown(m) != m.Id && !InternalId(m.Id)) p.Children.Add(Ui.Text($"아이디: {m.Id}", "muted"));
        p.Children.Add(Ui.Text($"가입일 {m.Created.LocalDateTime:yyyy-MM-dd}", "muted"));
        if (m.TicketPercent is int percent)
            p.Children.Add(Ui.Text($"할인 티켓 {percent}% · {m.TicketExpires?.LocalDateTime:yyyy-MM-dd}까지 · 플랜 화면의 계좌이체 · 직접 결제에서 쓸 수 있어요(판매자가 그 플랜에 열어 둔 경우, 묶음 키 제외)", "label", wrap: true));
        if (!_features.Totp) { p.Children.Add(LogOut()); return p; }   // older server: no device list to log out from
        if (x.Email != null && x.EmailVerified) return p;
        if (x.Email != null)
        {
            p.Children.Add(Ui.Text($"이메일: {x.Email} · 확인 대기 중", "label", wrap: true));
            p.Children.Add(Ui.Text("보낸 메일의 링크를 누르면 이메일이 추가돼요. 메일이 오지 않았다면 아래에서 다시 보낼 수 있어요.", "muted", wrap: true));
        }
        AddEmail(p, m);
        return p;
    }

    /// 아이디 accounts: an email to log in with, find the password by and get security alerts at. Optional.
    private void AddEmail(StackPanel p, AccountProfile m)
    {
        p.Children.Add(Heading("이메일 추가"));
        p.Children.Add(Ui.Text("이메일은 선택이에요. 추가하면 이메일로도 로그인하고 비밀번호를 찾을 수 있고, 새 기기 로그인 같은 보안 알림을 받아요. 이메일은 로그인, 비밀번호 재설정, 보안 알림에만 써요.", "muted", wrap: true));
        if (!_features.Email) { p.Children.Add(Ui.Text("지금은 메일을 보낼 수 없어서 이메일을 추가할 수 없어요. 잠시 뒤 다시 해 주세요.", "muted", wrap: true)); return; }
        var email = Input("이메일", max: AccountRules.MaxEmail);
        var password = Input("비밀번호", secret: true);
        var go = Ui.Button("확인 메일 보내기", "ghost");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            var mail = email.Text ?? "";
            if (!AccountRules.ValidEmail(mail)) { msg.Text = "이메일 주소를 확인해 주세요"; return; }
            if (await Run(go, msg, () => Account.AddEmailAsync(mail, password.Text ?? "")))
                msg.Text = "확인 메일을 보냈어요. 메일의 링크를 누르면 이메일이 추가돼요.";
        };
        p.Children.Add(email);
        p.Children.Add(password);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
    }

    // ------------------------------------------------------------ 내 라이선스
    private Control Keys()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("내 라이선스", "h2"));
        p.Children.Add(Ui.Text("이 계정에 연결된 키예요. 키는 계정 없이도 키만으로 쓸 수 있어요.", "muted", wrap: true));
        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(Ui.Text("불러오는 중…", "muted"));
        p.Children.Add(list);
        LoadKeys(list, _shown);
        return p;
    }

    private async void LoadKeys(StackPanel list, int mine)
    {
        var (keys, error) = await Account.KeysAsync();
        if (mine != _shown) return;
        list.Children.Clear();
        if (keys == null) { list.Children.Add(Ui.Text(error ?? "", "muted", wrap: true)); return; }
        if (keys.Count == 0)
        {
            list.Children.Add(Ui.Text("연결된 키가 없어요. 아래 '가진 키 연결하기'로 가진 키를 연결할 수 있어요.", "muted", wrap: true));
            return;
        }
        var here = License.KeyId;
        foreach (var k in keys) list.Children.Add(KeyRow(k, k.Id == here));
    }

    private Control KeyRow(AccountKey k, bool here)
    {
        var p = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text(License.PlanName(k.Plan), "label"));
        bool over = k.Expires is { } end && end < DateTimeOffset.Now;
        if (k.Revoked) head.Children.Add(Spaced(Ui.Tag("막힌 키")));
        else if (over) head.Children.Add(Spaced(Ui.Tag("기간 끝남")));
        else if (here) head.Children.Add(Ui.Badge("이 PC에서 사용 중"));
        p.Children.Add(head);
        var period = k.Expires is { } until ? $"사용 기간: {until.LocalDateTime:yyyy-MM-dd}까지" : "사용 기간: 기간 제한 없음";
        p.Children.Add(Ui.Text(k.Issued is { } issued ? $"받은 날: {issued.LocalDateTime:yyyy-MM-dd} · {period}" : period, "muted", wrap: true));
        p.Children.Add(Ui.Text($"키 식별 번호: {k.Id}", "muted"));
        if (k.Key.Length > 0)
        {
            var copy = Ui.Button("키 복사", "ghost");
            var copied = Message();
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } board) { await board.SetTextAsync(k.Key); copied.Text = "복사했어요"; }
            };
            p.Children.Add(Row(copy, copied));
            copied.VerticalAlignment = VerticalAlignment.Center;
        }
        return Ui.Card(p, "tile");
    }

    private static Control Spaced(Control tag)
    {
        tag.Margin = new Thickness(8, 0, 0, 0);
        return tag;
    }

    // ------------------------------------------------------------ 가진 키 연결하기
    private Control LinkKey()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("가진 키 연결하기", "h2"));
        p.Children.Add(Ui.Text("카드로 산 키를 이 계정에 연결해요. 키를 넣으면 그 키를 산 이메일로 확인 메일이 가고, 메일의 링크를 누르면 연결돼요. 계좌이체로 산 키는 고객센터(디스코드 #help)에서 입금자명과 입금 날짜를 확인한 뒤 연결해 드려요.", "muted", wrap: true));
        if (!_features.Email) { p.Children.Add(Ui.Text("지금은 메일을 보낼 수 없어서 키를 연결할 수 없어요. 잠시 뒤 다시 해 주세요.", "muted", wrap: true)); return p; }
        var key = Input("라이선스 키", max: 4000);
        var go = Ui.Button("확인 메일 보내기", "primary");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            if ((key.Text ?? "").Trim().Length == 0) { msg.Text = "연결할 키를 넣어 주세요"; return; }
            if (await Run(go, msg, () => Account.LinkKeyAsync(key.Text ?? "")))
                msg.Text = "키를 산 이메일로 확인 메일을 보냈어요. 메일의 링크를 누르면 연결돼요.";
        };
        p.Children.Add(key);
        if (License.KeyText() is { } mine)
        {
            var fill = Ui.Button("이 PC의 키 넣기", "ghost");
            fill.Click += (_, _) => key.Text = mine;
            p.Children.Add(Row(go, fill));
        }
        else p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }

    // ------------------------------------------------------------ 로그인된 기기
    private Control Devices()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("로그인된 기기", "h2"));
        p.Children.Add(Ui.Text("이 계정으로 로그인한 기기예요. 모르는 기기가 있으면 그 기기를 로그아웃하고 비밀번호를 바꿔 주세요.", "muted", wrap: true));
        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(Ui.Text("불러오는 중…", "muted"));
        p.Children.Add(list);
        var msg = Message();
        LoadDevices(list, msg, _shown);
        var me = Ui.Button("이 기기 로그아웃", "ghost");
        me.Click += async (_, _) => { me.IsEnabled = false; await Account.LogOutAsync(); };
        var all = Ui.Button("모든 기기 로그아웃", "ghost");
        all.Click += async (_, _) =>
        {
            if (all.Tag is not "armed")
            {   // two clicks: every session ends, this one too
                all.Tag = "armed";
                all.Content = "한 번 더 누르면 이 기기까지 모두 로그아웃해요";
                return;
            }
            await Run(all, msg, Account.LogOutAllAsync);
        };
        p.Children.Add(Row(me, all));
        p.Children.Add(msg);
        return p;
    }

    private async void LoadDevices(StackPanel list, TextBlock msg, int mine)
    {
        var (devices, error) = await Account.DevicesAsync();
        if (mine != _shown) return;
        list.Children.Clear();
        if (devices == null) { list.Children.Add(Ui.Text(error ?? "", "muted", wrap: true)); return; }
        foreach (var d in devices)
        {
            var p = new StackPanel { Spacing = 4 };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Ui.Text(d.Name, "label"));
            if (d.Current) head.Children.Add(Ui.Badge("이 기기"));
            p.Children.Add(head);
            var seen = d.LastSeen ?? d.Created;
            var line = string.Join(" · ", new[] { Country(d.Country), seen is { } at ? $"마지막 접속 {at.LocalDateTime:yyyy-MM-dd HH:mm}" : null }
                                               .Where(s => !string.IsNullOrEmpty(s)));
            if (line.Length > 0) p.Children.Add(Ui.Text(line, "muted"));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(p);
            if (!d.Current)
            {
                var off = Ui.Button("로그아웃", "ghost");
                off.VerticalAlignment = VerticalAlignment.Center;
                off.Click += async (_, _) =>
                {
                    if (await Run(off, msg, () => Account.LogOutDeviceAsync(d.Id))) LoadDevices(list, msg, mine);
                };
                Grid.SetColumn(off, 1);
                grid.Children.Add(off);
            }
            list.Children.Add(Ui.Card(grid, "tile"));
        }
    }

    /// The country's name in the screen's language; the code itself when the system does not know it.
    private static string Country(string code)
    {
        if (code.Length != 2) return "";
        try
        {
            var region = new RegionInfo(code);
            return Lang.Translating ? region.EnglishName : region.DisplayName;
        }
        catch (ArgumentException) { return code; }
    }
}
