// Frame rates for the on-screen FPS display (Pages/FpsOverlay.cs). sb_helper counts the Present calls
// Windows reports through event tracing (native/helper/src/fpslive.cpp), the source PresentMon and the
// Xbox Game Bar use: the game is never opened or touched. Event tracing needs an administrator or a
// member of Performance Log Users, so the plain helper is tried first and then the admin helper,
// which asks once per app run (or not at all when another feature has already started it).
using System.Globalization;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public sealed partial class WindowsBoostBackend
{
    private BoostHelper? _fpsHelper;

    public bool FpsLiveRunning => _fpsHelper?.Running == true;

    /// null = counting, otherwise the text to show.
    public async Task<string?> FpsLiveStartAsync()
    {
        if (FpsLiveRunning) return null;
        _fpsHelper = null;
        if (await _plainHelper.EnsureAsync() == null && (await _plainHelper.CallAsync("fps.live.start")).ok)
        {
            _fpsHelper = _plainHelper;
            return null;
        }
        var e = await _helper.EnsureAsync();
        if (e != null)
            return e.StartsWith("관리자 권한을 허용하지") ? "관리자 권한을 허용하지 않아서 FPS를 잴 수 없어요" : e.Replace(" (바뀐 것 없음)", "");
        var (ok, data) = await _helper.CallAsync("fps.live.start");
        if (!ok) return $"FPS를 잴 수 없어요 ({data})";
        _fpsHelper = _helper;
        return null;
    }

    /// Frames a second over the last second, the 1% low, and how many seconds the process has drawn
    /// without a pause; null when it has not drawn lately or the helper is gone.
    public async Task<(double Fps, double Low, double Steady)?> FpsLiveReadAsync(int pid)
    {
        if (_fpsHelper is not { Running: true } h) return null;
        var (ok, data) = await h.CallAsync("fps.live", pid.ToString(CultureInfo.InvariantCulture));
        if (!ok) return null;
        var p = data.Split(';');
        static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (p.Length != 3 || !Num(p[0], out var fps) || !Num(p[1], out var low) || !Num(p[2], out var steady) || fps <= 0) return null;
        return (fps, low, steady);
    }

    public async Task FpsLiveStopAsync()
    {
        var h = _fpsHelper;
        _fpsHelper = null;
        if (h is { Running: true }) await h.CallAsync("fps.live.stop");
    }
}
