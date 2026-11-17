// 2단계 인증 on the account page: turning it on (password -> QR code and the secret as text -> one code -> 10
// recovery codes shown once), making new recovery codes, turning it off. The secret and the codes live in this
// page's memory only, and on disk only where the member saves or prints them; nothing logs them.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SystemBoost.App.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SystemBoost.App.Pages;

public sealed partial class AccountPage
{
    private string[]? _backup;      // recovery codes just made: shown once, until the member says they are kept
    private string? _backupFor;     // whose they are, for the saved or printed copy
    private TotpSetup? _setup;      // turning 2단계 인증 on: the QR step

    private static readonly FontFamily Mono = new("Consolas, Menlo, monospace");

    private Control TwoFactor(AccountProfile m)
    {
        var x = m.Extras ?? new AccountExtras(null, false, false, null);
        var p = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text("2단계 인증", "h2"));
        if (x.TwoFa) head.Children.Add(Ui.Badge("켜짐"));
        else { var off = Ui.Tag("꺼짐"); off.Margin = new Thickness(8, 0, 0, 0); head.Children.Add(off); }
        p.Children.Add(head);
        if (_setup is { } setup) { SetupStep(p, setup, Shown(m)); return p; }
        if (!x.TwoFa) { TurnOn(p); return p; }

        p.Children.Add(Ui.Text("로그인할 때 비밀번호와 함께 인증 앱의 6자리 코드를 넣어요. 휴대폰을 잃으면 복구 코드로 로그인할 수 있어요.", "muted", wrap: true));
        if (x.BackupLeft is int left)
        {
            p.Children.Add(Ui.Text($"남은 복구 코드 {left}개", "label"));
            if (left <= AccountRules.BackupFew)
                p.Children.Add(Ui.Text("복구 코드가 얼마 남지 않았어요. 아래에서 새로 만들어 두세요.", "label accent", wrap: true));
        }

        p.Children.Add(Heading("복구 코드 새로 만들기"));
        p.Children.Add(Ui.Text("새로 만들면 지금 복구 코드는 모두 못 쓰게 돼요. 비밀번호를 다시 확인해요.", "muted", wrap: true));
        var pw = Input("비밀번호", secret: true);
        var make = Ui.Button("복구 코드 새로 만들기", "ghost");
        var made = Message();
        make.Click += async (_, _) =>
        {
            string[]? codes = null;
            await Run(make, made, async () =>
            {
                var (fresh, error) = await Account.BackupNewAsync(pw.Text ?? "");
                codes = fresh;
                return error;
            });
            if (codes != null) { _backup = codes; _backupFor = Shown(m); Show(); }
        };
        p.Children.Add(pw);
        p.Children.Add(Left(make));
        p.Children.Add(made);

        p.Children.Add(Heading("2단계 인증 끄기"));
        p.Children.Add(Ui.Text("비밀번호와 인증 앱의 코드(또는 복구 코드 하나)를 넣어 주세요. 끄면 복구 코드도 모두 못 쓰게 돼요.", "muted", wrap: true));
        var offPw = Input("비밀번호", secret: true);
        var offCode = Input("인증 앱 코드 또는 복구 코드", max: 24);
        var turnOff = Ui.Button("2단계 인증 끄기", "ghost");
        var offMsg = Message();
        turnOff.Click += async (_, _) =>
        {
            var code = AccountRules.TotpCode(offCode.Text) ?? AccountRules.BackupCode(offCode.Text);
            if (code == null) { offMsg.Text = "인증 앱의 6자리 숫자나 복구 코드(XXXX-XXXX-XXXX)를 넣어 주세요"; return; }
            if (await Run(turnOff, offMsg, () => Account.TotpOffAsync(offPw.Text ?? "", code))) Show();
        };
        p.Children.Add(offPw);
        p.Children.Add(offCode);
        p.Children.Add(Left(turnOff));
        p.Children.Add(offMsg);
        return p;
    }

    private void TurnOn(StackPanel p)
    {
        p.Children.Add(Ui.Text("로그인할 때 비밀번호와 함께 휴대폰 인증 앱(Google Authenticator, Microsoft Authenticator 등)의 6자리 코드를 넣어요. 비밀번호가 새어도 다른 사람이 로그인할 수 없어요.", "muted", wrap: true));
        var pw = Input("비밀번호", secret: true);
        var go = Ui.Button("켜기", "primary");
        var msg = Message();
        go.Click += async (_, _) =>
        {
            TotpSetup? setup = null;
            await Run(go, msg, async () =>
            {
                var (s, error) = await Account.TotpStartAsync(pw.Text ?? "");
                setup = s;
                return error;
            });
            if (setup != null) { _setup = setup; Show(); }
        };
        p.Children.Add(pw);
        p.Children.Add(Left(go));
        p.Children.Add(msg);
    }

    private void SetupStep(StackPanel p, TotpSetup setup, string who)
    {
        p.Children.Add(Ui.Text("1. 휴대폰의 인증 앱에서 '+'를 누르고 이 QR 코드를 찍어 주세요.", "label", wrap: true));
        try
        {
            var qr = new QrView(QrCode.FromText(setup.Uri));
            p.Children.Add(Left(qr));
        }
        catch (ArgumentException) { }   // too long for a QR code (cannot happen with the server's address): the key below still works
        p.Children.Add(Ui.Text("QR 코드를 찍을 수 없으면 인증 앱에서 '설정 키 입력'을 고르고 이 키를 넣어 주세요 (시간 기준, 6자리).", "muted", wrap: true));
        var secret = new SelectableTextBlock { Text = AccountRules.GroupSecret(setup.Secret), FontFamily = Mono, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        var copy = Ui.Button("복사", "ghost");
        var copied = Message();
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } board) await board.SetTextAsync(setup.Secret);
            copied.Text = "복사했어요";
        };
        p.Children.Add(Row(secret, copy));
        p.Children.Add(copied);
        p.Children.Add(Ui.Text("2. 인증 앱에 나온 6자리 코드를 넣어 주세요.", "label", wrap: true));
        var code = Input("6자리 코드", max: 8);
        var go = Ui.Button("확인하고 켜기", "primary");
        var cancel = Ui.Button("취소", "ghost");
        var msg = Message();
        async void Go()
        {
            if (!go.IsEnabled) return;
            if (AccountRules.TotpCode(code.Text) is not { } digits) { msg.Text = "인증 앱의 6자리 숫자를 넣어 주세요"; return; }
            string[]? codes = null;
            await Run(go, msg, async () =>
            {
                var (fresh, error) = await Account.TotpConfirmAsync(digits);
                codes = fresh;
                return error;
            });
            if (codes != null) { _setup = null; _backup = codes; _backupFor = who; Show(); }
        }
        go.Click += (_, _) => Go();
        OnEnter(code, Go);
        cancel.Click += (_, _) => { _setup = null; Show(); };
        p.Children.Add(code);
        p.Children.Add(Row(go, cancel));
        p.Children.Add(msg);
    }

    // ------------------------------------------------------------ the recovery codes, shown once
    private string BackupFileText(string[] codes) => AccountReplies.BackupText(codes,
        Lang.T("SystemBoost 2단계 인증 복구 코드") + (_backupFor is { } who ? $" · {who}" : "") + $" · {DateTime.Now:yyyy-MM-dd}",
        Lang.T("인증 앱을 쓸 수 없을 때 로그인에 써요. 코드 하나는 한 번만 쓸 수 있어요."));

    private Control BackupCard()
    {
        var codes = _backup!;
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Ui.Text($"복구 코드 {codes.Length}개를 보관하세요", "h2 accent"));
        p.Children.Add(Ui.Text("휴대폰을 잃거나 인증 앱을 쓸 수 없을 때 로그인에 쓰는 코드예요. 코드 하나는 한 번만 쓸 수 있어요. 이 화면을 닫으면 다시 볼 수 없어요.", "label", wrap: true));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < codes.Length; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var t = new SelectableTextBlock { Text = codes[i], FontFamily = Mono, FontSize = 18, Margin = new Thickness(0, 2, 40, 2) };
            Grid.SetRow(t, i / 2);
            Grid.SetColumn(t, i % 2);
            grid.Children.Add(t);
        }
        p.Children.Add(Ui.Card(grid, "note"));
        var status = Message();
        var copy = Ui.Button("복사", "ghost");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } board) { await board.SetTextAsync(BackupFileText(codes)); status.Text = "복사했어요"; }
        };
        var save = Ui.Button("텍스트 파일로 저장", "ghost");
        save.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            try
            {
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Lang.T("복구 코드 저장"), SuggestedFileName = Lang.T("SystemBoost 복구 코드.txt"), DefaultExtension = "txt",
                    FileTypeChoices = new[] { new FilePickerFileType(Lang.T("텍스트")) { Patterns = new[] { "*.txt" } } },
                });
                if (file == null) return;
                await using var s = await file.OpenWriteAsync();
                await using var w = new StreamWriter(s, new UTF8Encoding(true));
                await w.WriteAsync(BackupFileText(codes));
                status.Text = "저장했어요";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "저장하지 못했어요. 다른 곳을 골라 주세요"; }
        };
        var print = Ui.Button("인쇄", "ghost");
        print.Click += (_, _) => status.Text = Print(BackupFileText(codes)) ?? "프린터로 보냈어요. 인쇄된 종이를 확인해 주세요";
        p.Children.Add(Row(copy, save, print));
        p.Children.Add(status);
        var kept = new CheckBox { Content = "복구 코드를 안전한 곳에 저장했어요" };
        var next = Ui.Button("계속", "primary");
        next.IsEnabled = false;
        kept.IsCheckedChanged += (_, _) => next.IsEnabled = kept.IsChecked == true;
        next.Click += (_, _) => { _backup = null; _backupFor = null; Show(); };
        p.Children.Add(kept);
        p.Children.Add(Left(next));
        return p;
    }

    // ------------------------------------------------------------ printing: a file Windows prints, deleted after
    private const string PrintPrefix = "print-codes-";

    /// Null when the print went out, else what to do instead. The file sits in the app's data folder only
    /// while the printing program reads it.
    private static string? Print(string text)
    {
        string file = Path.Combine(Platform.DataDir, $"{PrintPrefix}{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(file, text, new UTF8Encoding(true));
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(file) { UseShellExecute = true, Verb = "print", WindowStyle = ProcessWindowStyle.Hidden }
                : new ProcessStartInfo("lp") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { file } };
            _ = DeleteLater(file, Process.Start(start));
            return null;
        }
        catch (Exception)
        {
            TryDelete(file);
            return "인쇄를 시작하지 못했어요. '텍스트 파일로 저장'한 뒤 그 파일을 인쇄해 주세요";
        }
    }

    // Notepad and its kin may hand the file to another process and end at once, so the file waits a little longer.
    private static async Task DeleteLater(string file, Process? process)
    {
        try
        {
            if (process != null)
                using (process)
                using (var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                {
                    try { await process.WaitForExitAsync(limit.Token); }
                    catch (OperationCanceledException) { }
                }
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        catch (Exception) { }
        finally { TryDelete(file); }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception) { }
    }

    /// Print files left by an app that closed before it could delete them.
    private static void SweepPrintFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Platform.DataDir, PrintPrefix + "*.txt"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-10)) TryDelete(f);
        }
        catch (Exception) { }
    }
}

/// A QR code drawn black on white with the four-module quiet zone scanners need (the page itself is black).
internal sealed class QrView : Control
{
    private const int Quiet = 4;
    private readonly QrCode _qr;
    private readonly Geometry _dark;

    public QrView(QrCode qr, int module = 5)
    {
        _qr = qr;
        Width = Height = (qr.Size + 2 * Quiet) * module;
        AutomationProperties.SetName(this, "QR 코드");
        // One geometry for all dark modules: separate squares would leave hairline seams between them.
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (int y = 0; y < qr.Size; y++)
                for (int x = 0; x < qr.Size; x++)
                {
                    if (!qr.Dark(x, y)) continue;
                    int end = x;
                    while (end + 1 < qr.Size && qr.Dark(end + 1, y)) end++;
                    double left = (x + Quiet) * module, top = (y + Quiet) * module, right = (end + 1 + Quiet) * module, bottom = top + module;
                    c.BeginFigure(new Point(left, top), true);
                    c.LineTo(new Point(right, top));
                    c.LineTo(new Point(right, bottom));
                    c.LineTo(new Point(left, bottom));
                    c.EndFigure(true);
                    x = end;
                }
        }
        _dark = g;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(0, 0, Width, Height));
        context.DrawGeometry(Brushes.Black, null, _dark);
    }
}
