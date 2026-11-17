// "코드·키 찾는 방법": every way back to a key, a password or an account, in plain words (owner's table B-4).
// Opened from the login form; the buttons below lead to the forms it talks about.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SystemBoost.App.Pages;

public sealed partial class AccountPage
{
    private static readonly (string What, string How)[] HelpRows =
    {
        ("내 라이선스 키", "로그인한 뒤 '계정' 화면의 '내 라이선스'에서 보고 복사할 수 있어요."),
        ("로그인 없이 키 다시 받기", "'키 다시 받기'에 키를 살 때 쓴 이메일을 넣으면 그 이메일로 키를 보내요. 가입 여부를 알 수 없도록 결과 문구는 언제나 같아요."),
        ("계좌이체로 산 키", "고객센터(디스코드 #help)에 입금자명과 입금 날짜를 알려 주세요. 판매자가 확인한 뒤 키를 다시 보내 드려요."),
        ("비밀번호", "'비밀번호 찾기'에 가입한 이메일을 넣으면 재설정 링크를 보내요. 링크는 30분 동안 한 번만 쓸 수 있어요. 아이디로 가입했다면 그때 받은 복구 코드로 바꿔요."),
        ("2단계 인증 휴대폰을 잃어버림", "로그인할 때 '복구 코드로 로그인'을 누르고, 2단계 인증을 켤 때 받은 복구 코드 하나를 넣어요."),
        ("복구 코드까지 잃어버림", "'계정 복구 요청'을 보내 주세요. 판매자가 결제 기록으로 본인인지 확인하고, 72시간을 기다린 뒤 처리해요. 그 사이 원래 이메일로 알림이 가요."),
    };

    private Control Help()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text("코드·키 찾는 방법", "h2"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        void Line(int row, string left, string right, string cls)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var a = Ui.Text(left, cls, wrap: true);
            var b = Ui.Text(right, cls == "muted" ? "muted" : "label", wrap: true);
            a.Margin = new Thickness(0, 8, 16, 8);
            b.Margin = new Thickness(0, 8, 0, 8);
            Grid.SetRow(a, row);
            Grid.SetRow(b, row);
            Grid.SetColumn(b, 1);
            grid.Children.Add(a);
            grid.Children.Add(b);
        }
        Line(0, "찾는 것", "방법", "muted");
        for (int i = 0; i < HelpRows.Length; i++) Line(i + 1, HelpRows[i].What, HelpRows[i].How, "label accent");
        p.Children.Add(grid);
        var note = Ui.Card(Ui.Text("복구 코드는 두 가지예요. 아이디로 가입할 때 받은 코드(XXXX-XXXX-XXXX-XXXX, 1개)는 비밀번호를 바꿀 때 쓰고, 2단계 인증을 켤 때 받은 코드(XXXX-XXXX-XXXX, 10개)는 로그인할 때 써요.", "muted", wrap: true), "note");
        p.Children.Add(note);
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        void Go(string text, Mode mode, string cls = "ghost")
        {
            var b = Ui.Button(text, cls);
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Click += (_, _) => { _mode = mode; Show(); };
            bar.Children.Add(b);
        }
        Go("키 다시 받기", Mode.Resend);
        Go("비밀번호 찾기", Mode.Recover);
        Go("계정 복구 요청", Mode.Request);
        Go("로그인으로", Mode.LogIn, "primary");
        p.Children.Add(bar);
        return p;
    }
}
