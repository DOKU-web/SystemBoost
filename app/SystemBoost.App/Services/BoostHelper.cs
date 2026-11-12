// Talks to sb_helper.exe, the small elevated program behind the admin-only
// features (native/helper). It starts with one UAC prompt the first time it is
// needed and exits together with the app. See native/helper/src/main.cpp for
// how it checks its caller and what it accepts. Plain (elevated: false), it starts
// without a prompt and can only do what this user may do.
// The administrator prompt never comes up over a game: while a game or any full-screen program is in front,
// EnsureAsync changes nothing and says so (Deferred), and the next try after the game asks.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

internal sealed class BoostHelper : IDisposable
{
    private const int ConnectTimeoutMs = 15000;
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "sb_helper.exe");

    /// The one elevated helper of this app run, shared by the boost features and the page file setting,
    /// so one prompt covers both.
    public static BoostHelper Admin { get; } = new();

    /// What EnsureAsync answers while a game is in front.
    public const string DeferredText = "게임 화면이 앞에 있어서 관리자 확인 창을 띄우지 않았어요. 게임을 끈 뒤 다시 해 주세요 (바뀐 것 없음)";

    /// A prompt was held back because a game was in front. May fire on any thread.
    public static event Action? Deferred;

    private readonly bool _elevated;
    public BoostHelper(bool elevated = true) { _elevated = elevated; }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public bool Running => _pipe?.IsConnected == true;

    /// null = ready, otherwise the text to show. Nothing changed in either case.
    public async Task<string?> EnsureAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Running) return null;
            if (!File.Exists(HelperPath)) return "관리자 도우미 파일이 없어요. 앱을 다시 설치해 주세요 (바뀐 것 없음)";
            // the secure desktop would take a game off the screen in the middle of a match
            if (_elevated && !Environment.IsPrivilegedProcess && ResourceManagers.FullscreenInFront())
            {
                Deferred?.Invoke();
                return DeferredText;
            }
            var name = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var psi = new ProcessStartInfo(HelperPath, $"--pipe {name} --parent {Environment.ProcessId}")
            { UseShellExecute = true, Verb = _elevated ? "runas" : "", WindowStyle = ProcessWindowStyle.Hidden };
            try { await Task.Run(() => { using var p = Process.Start(psi); }); }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return "관리자 권한을 허용하지 않아서 취소했어요 (바뀐 것 없음)"; }
            catch (Win32Exception) { return "관리자 도우미를 실행하지 못했어요 (바뀐 것 없음)"; }

            var pipe = new NamedPipeClientStream(".", "sb-helper-" + name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(ConnectTimeoutMs); }
            catch (Exception) { pipe.Dispose(); return "관리자 도우미에 연결하지 못했어요 (바뀐 것 없음)"; }
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false));
            _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var (ok, _) = await ExchangeAsync("ping");
            if (ok) return null;
            Close();
            return "관리자 도우미가 응답하지 않아요 (바뀐 것 없음)";
        }
        finally { _gate.Release(); }
    }

    /// Sends one request. Fields must not contain tabs or newlines.
    public async Task<(bool ok, string data)> CallAsync(params string[] fields)
    {
        await _gate.WaitAsync();
        try { return Running ? await ExchangeAsync(string.Join('\t', fields)) : (false, "not running"); }
        finally { _gate.Release(); }
    }

    private async Task<(bool ok, string data)> ExchangeAsync(string line)
    {
        try
        {
            await _writer!.WriteLineAsync(line);
            var reply = await _reader!.ReadLineAsync();
            if (reply == null) { Close(); return (false, "disconnected"); }
            int tab = reply.IndexOf('\t');
            return tab < 0 ? (false, reply) : (reply[..tab] == "ok", reply[(tab + 1)..]);
        }
        catch (IOException) { Close(); return (false, "disconnected"); }
    }

    private void Close()
    {
        _reader = null;
        _writer = null;
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose() => Close();
}
