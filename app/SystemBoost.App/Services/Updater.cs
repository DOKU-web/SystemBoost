// Automatic updates for the direct-download builds (installer, portable, macOS) with Velopack.
// Releases live on GitHub Releases; CI packs them with `vpk pack` and uploads them (see build.yml).
//
//  - Checks 15 s after start and then every 6 hours (can be turned off in Settings).
//  - Downloads in the background (only the changed parts when a delta exists).
//  - Never interrupts: shows a bar; the user picks "restart now", or it is applied when the app closes.
//  - A background check that fails stays silent; errors show only after the user presses "check now".
//  - Steam builds and dev builds are not Velopack installs (IsInstalled = false): this does nothing there.
//  - Settings, license key and records are outside the app folder, so updates keep them.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace SystemBoost.App.Services;

public enum UpdateState { Unsupported, Idle, Checking, UpToDate, Downloading, Ready, Failed }

public static class Updater
{
    // Same repository as the download page (site/download.html). TODO: set the real one.
    public const string RepoUrl = "https://github.com/DOKU-web/SystemBoost";

    private static UpdateManager? _mgr;
    private static bool _mgrTried;
    private static UpdateManager? Mgr
    {
        get
        {
            if (_mgrTried) return _mgr;
            _mgrTried = true;
            try { _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
            catch (Exception) { _mgr = null; }
            return _mgr;
        }
    }

    /// Installed with the Velopack installer or portable zip (not Steam, not a dev build).
    public static bool Supported { get { try { return Mgr?.IsInstalled == true; } catch (Exception) { return false; } } }
    public static bool IsPortable { get { try { return Mgr?.IsPortable == true; } catch (Exception) { return false; } } }
    public static string CurrentVersion
    {
        get
        {
            try { if (Mgr?.CurrentVersion is { } v) return v.ToString(); } catch (Exception) { }
            var a = typeof(Updater).Assembly.GetName().Version;
            return a == null ? "?" : $"{a.Major}.{a.Minor}.{a.Build}";
        }
    }

    public static UpdateState State { get; private set; } = UpdateState.Idle;
    public static string? NewVersion { get; private set; }
    public static string ReleaseNotes { get; private set; } = "";
    public static int Progress { get; private set; }
    public static string? Error { get; private set; }
    public static DateTime? LastCheck { get; private set; }
    /// Raised on the UI thread whenever State / Progress changes.
    public static event Action? Changed;

    private static UpdateInfo? _info;
    private static readonly SemaphoreSlim Busy = new(1, 1);
    private static Timer? _timer;
    private static bool _userAsked;

    // ------------------------------------------------------------ settings
    private sealed record Prefs(bool Auto = true);
    private static string PrefsFile => Path.Combine(Platform.DataDir, "update.json");
    public static bool AutoCheck
    {
        get { try { return File.Exists(PrefsFile) ? JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsFile))?.Auto ?? true : true; } catch (Exception) { return true; } }
        set
        {
            try { File.WriteAllText(PrefsFile, JsonSerializer.Serialize(new Prefs(value))); } catch (Exception) { }
            if (value) Start(); else { _timer?.Dispose(); _timer = null; }
        }
    }

    /// Call once at app start.
    public static void Start()
    {
        if (!Supported) { State = UpdateState.Unsupported; return; }
        if (!AutoCheck || _timer != null) return;
        _timer = new Timer(_ => _ = CheckAndDownloadAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromHours(6));
    }

    /// Checks GitHub Releases; if a newer version exists, downloads it in the background.
    /// userAsked: the user pressed "check now", so a failure is shown instead of ignored.
    public static async Task CheckAndDownloadAsync(bool userAsked = false)
    {
        if (!Supported) { Set(UpdateState.Unsupported); return; }
        if (State is UpdateState.Ready) return;                       // already downloaded, waiting for restart
        if (userAsked) _userAsked = true;                             // also covers a check already running
        if (!await Busy.WaitAsync(0)) return;
        try
        {
            Error = null;
            Set(UpdateState.Checking);
            _info = await Mgr!.CheckForUpdatesAsync();
            LastCheck = DateTime.Now;
            if (_info == null) { Set(UpdateState.UpToDate); return; }
            NewVersion = _info.TargetFullRelease.Version.ToString();
            ReleaseNotes = _info.TargetFullRelease.NotesMarkdown ?? "";
            Progress = 0;
            Set(UpdateState.Downloading);
            await Mgr.DownloadUpdatesAsync(_info, p => { Progress = p; Set(UpdateState.Downloading); });
            Set(UpdateState.Ready);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            LastCheck = DateTime.Now;                                 // no releases published: nothing newer exists
            Set(UpdateState.UpToDate);
        }
        catch (Exception e)
        {
            if (!_userAsked) { Set(UpdateState.Idle); return; }
            Error = e is HttpRequestException ? "업데이트 서버에 연결하지 못했어요" : "업데이트를 확인하지 못했어요";
            Set(UpdateState.Failed);
        }
        finally { _userAsked = false; Busy.Release(); }
    }

    /// Restarts into the new version now. Call the app's own shutdown first (frame generation, resource limits).
    public static void ApplyAndRestart()
    {
        if (State != UpdateState.Ready || _info == null) return;
        _applied = true;
        Mgr!.ApplyUpdatesAndRestart(_info.TargetFullRelease);
    }

    private static bool _applied;
    /// Called when the app closes: a downloaded update is applied after exit, without restarting.
    public static void ApplyOnExit()
    {
        if (_applied || State != UpdateState.Ready || _info == null) return;
        _applied = true;
        try { Mgr!.WaitExitThenApplyUpdates(_info.TargetFullRelease, true, false); } catch (Exception) { }
    }

    private static void Set(UpdateState s)
    {
        State = s;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }
}
