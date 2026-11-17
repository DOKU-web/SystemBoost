// Account page, signed out: "키 다시 받기" (the key goes to the address it was bought with) and "계정 복구 요청"
// (the 2단계 인증 phone and the recovery codes are both gone). Both always answer with the same words, so
// nobody learns from them which addresses or accounts exist.
using Avalonia.Controls;
using SystemBoost.App.Services;

namespace SystemBoost.App.Pages;

public sealed partial class AccountPage
{
    private const string AskSupport = "지금은 앱에서 보낼 수 없어요. 고객센터(디스코드 #help)에 문의해 주세요.";

    private Control Resend()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("카드로 키를 살 때 쓴 이메일을 넣으면 그 이메일로 키를 다시 보내요. 로그인하지 않아도 돼요.", "muted", wrap: true));
        p.Children.Add(Ui.Text("계좌이체로 산 키는 고객센터(디스코드 #help)에 입금자명과 입금 날짜를 알려 주시면 확인한 뒤 다시 보내 드려요.", "muted", wrap: true));
        if (!_features.Email) { p.Children.Add(Ui.Text(AskSupport, "label", wrap: true)); return p; }
        var email = Input("구매할 때 쓴 이메일", max: AccountRules.MaxEmail);
        var go = Ui.Button("키 보내기", "primary");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            var mail = email.Text ?? "";
            if (!AccountRules.ValidEmail(mail)) { msg.Text = "이메일 주소를 확인해 주세요"; return; }
            if (await Run(go, msg, () => Account.ResendKeysAsync(mail)))
                msg.Text = "구매 이메일이 맞으면 그 이메일로 키를 보냈어요. 메일이 보이지 않으면 스팸함을 확인해 주세요.";
        };
        p.Children.Add(email);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }

    private Control Request()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("2단계 인증 휴대폰과 복구 코드를 모두 잃었을 때 보내요. 판매자가 결제 기록(주문번호, 결제일, 금액, 카드 끝 4자리 또는 입금자명)으로 본인인지 확인해요.", "muted", wrap: true));
        p.Children.Add(Ui.Text("요청이 들어오면 계정의 원래 이메일로 알림 메일이 가고, 72시간을 기다린 뒤 처리해요. 본인이 보낸 요청이 아니면 그 메일의 링크로 취소할 수 있어요.", "muted", wrap: true));
        if (!_features.Totp) { p.Children.Add(Ui.Text(AskSupport, "label", wrap: true)); return p; }
        var login = Input("아이디 또는 이메일 (필수)", max: AccountRules.MaxEmail);
        var contact = Input("연락받을 이메일 (선택)", max: AccountRules.MaxEmail);
        var order = Input("주문번호", max: 64);
        var paidOn = Input("결제일 (예: 2026-11-17)", max: 32);
        var amount = Input("금액 (예: 9,900원)", max: 32);
        var card = Input("카드 끝 4자리 또는 입금자명", max: 40);
        var note = Input("메모 (선택)", max: 1000);
        note.AcceptsReturn = true;
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        note.MinHeight = 70;
        var go = Ui.Button("복구 요청 보내기", "primary");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            var who = (login.Text ?? "").Trim();
            if (who.Length == 0) { msg.Text = "아이디나 이메일을 넣어 주세요"; return; }
            if (AccountRules.IsEmail(who) && !AccountRules.ValidEmail(who)) { msg.Text = "이메일 주소를 확인해 주세요"; return; }
            var reply = (contact.Text ?? "").Trim();
            if (reply.Length > 0 && !AccountRules.ValidEmail(reply)) { msg.Text = "연락받을 이메일 주소를 확인해 주세요"; return; }
            var request = new RecoveryRequest(who, reply, order.Text ?? "", paidOn.Text ?? "", amount.Text ?? "", card.Text ?? "", note.Text ?? "");
            if (await Run(go, msg, () => Account.RecoveryRequestAsync(request)))
            {
                msg.Text = "요청을 받았어요. 72시간을 기다린 뒤 판매자가 결제 기록을 확인하고 처리해요. 계정에 이메일이 있으면 그 주소로 알림 메일이 가요. 본인이 보낸 요청이 아니면 그 메일의 링크로 취소할 수 있어요.";
                go.IsEnabled = false;   // one request is enough; the page opened again starts a new one
            }
        };
        foreach (var box in new[] { login, contact, order, paidOn, amount, card, note }) p.Children.Add(box);
        p.Children.Add(Ui.Text("보낸 내용은 본인 확인과 계정 복구에만 써요.", "muted", wrap: true));
        p.Children.Add(Left(go));
        p.Children.Add(msg);
        return p;
    }
}
