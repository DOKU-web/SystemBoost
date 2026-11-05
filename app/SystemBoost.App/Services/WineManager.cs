// macOS: run Windows games through Wine (compatibility layer, not emulation of a PC).
//  - Runtime: unmodified Wine build + DXVK-macOS, downloaded on first use from
//    their GitHub releases, verified against SHA-256 from wine-manifest.json.
//  - D3DMetal (Apple Game Porting Toolkit, needed for most DirectX 12 games) is
//    not redistributed: the user installs it from Apple and selects its folder.
//  - Games with kernel anti-cheat (EasyAntiCheat, BattlEye, Vanguard...) generally
//    refuse to run under Wine on macOS; the UI says so up front.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public enum GraphicsBackend { WineD3D, Dxvk, D3DMetal }

public sealed class WineGame
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";          // path inside or outside the bottle
    public string Arguments { get; set; } = "";
    public GraphicsBackend Backend { get; set; } = GraphicsBackend.Dxvk;
    public override string ToString() => Name;
}

public sealed record RuntimeAsset(string Name, string Url, string Sha256, string Tag);

public sealed class WineManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public string Root => Path.Combine(Platform.DataDir, "wine");
    public string RuntimeDir => Path.Combine(Root, "runtime");
    public string DxvkDir => Path.Combine(Root, "dxvk");
    public string Bottle => Path.Combine(Root, "bottles", "default");
    private string GamesFile => Path.Combine(Root, "games.json");
    private string GptkFile => Path.Combine(Root, "gptk-path.txt");

    public List<WineGame> Games { get; private set; } = new();

    public WineManager()
    {
        Directory.CreateDirectory(Root);
        try { if (File.Exists(GamesFile)) Games = JsonSerializer.Deserialize<List<WineGame>>(File.ReadAllText(GamesFile)) ?? new(); }
        catch (Exception) { Games = new(); }
    }

    public void SaveGames() => File.WriteAllText(GamesFile, JsonSerializer.Serialize(Games));

    // ------------------------------------------------------------ status
    public string? WineBinary => FindWine(RuntimeDir);
    public bool RuntimeInstalled => WineBinary != null;
    public bool DxvkInstalled => Directory.Exists(DxvkDir) && Directory.EnumerateFiles(DxvkDir, "d3d11.dll", SearchOption.AllDirectories).Any();
    public bool BottleReady => Directory.Exists(Path.Combine(Bottle, "drive_c", "windows"));

    /// Folder of a user-installed Game Porting Toolkit environment (contains bin/wine64 or bin/wine).
    public string? GptkPath
    {
        get => File.Exists(GptkFile) ? File.ReadAllText(GptkFile).Trim() : null;
        set { if (value == null) File.Delete(GptkFile); else File.WriteAllText(GptkFile, value); }
    }
    public string? GptkWine => GptkPath is string p ? FindWine(p) : null;

    private static string? FindWine(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        // wine-11 macOS builds use one "wine" (new WoW64); older ones ship wine64
        foreach (var name in new[] { "wine64", "wine" })
        {
            var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories)
                               .FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) == "bin");
            if (hit != null) return hit;
        }
        return null;
    }

    public static (RuntimeAsset? wine, RuntimeAsset? dxvk) Manifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "wine-manifest.json");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "wine-manifest.json");
        if (!File.Exists(path)) return (null, null);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        RuntimeAsset? Read(string key) =>
            doc.RootElement.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.Object
                ? new RuntimeAsset(e.GetProperty("name").GetString()!, e.GetProperty("url").GetString()!,
                                   e.GetProperty("sha256").GetString() ?? "", e.GetProperty("tag").GetString() ?? "")
                : null;
        return (Read("wine"), Read("dxvk"));
    }

    // ------------------------------------------------------------ Rosetta
    /// Apple Silicon needs Rosetta 2 for the x86-64 Wine/game code.
    public static bool NeedsRosetta => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                                       System.Runtime.InteropServices.Architecture.Arm64;

    public static async Task<bool> RosettaInstalledAsync()
    {
        if (!NeedsRosetta) return true;
        var (code, _) = await RunAsync("/usr/bin/arch", "-x86_64 /usr/bin/true", null, CancellationToken.None);
        return code == 0;
    }

    /// Opens Terminal with Apple's installer command (user sees and accepts Apple's license there).
    public static void OpenRosettaInstaller()
    {
        var script = "tell application \"Terminal\" to do script \"softwareupdate --install-rosetta\"";
        Process.Start(new ProcessStartInfo("/usr/bin/osascript") { ArgumentList = { "-e", script }, UseShellExecute = false });
    }

    // ------------------------------------------------------------ install
    public async Task InstallAsync(IProgress<(string step, double pct)> progress, CancellationToken ct)
    {
        var (wine, dxvk) = Manifest();
        if (wine == null) throw new InvalidOperationException("Wine 다운로드 정보(wine-manifest.json)가 없어요");
        if (!RuntimeInstalled)
            await DownloadAndExtractAsync(wine, RuntimeDir, "Wine", progress, ct);
        if (dxvk != null && !DxvkInstalled)
            await DownloadAndExtractAsync(dxvk, DxvkDir, "DXVK", progress, ct);
        if (!BottleReady)
        {
            progress.Report(("Windows 환경(보틀) 만드는 중…", 0.95));
            Directory.CreateDirectory(Bottle);
            await RunAsync(WineBinary!, "wineboot --init", Env(GraphicsBackend.WineD3D), ct);
        }
        progress.Report(("준비 완료", 1.0));
    }

    private static async Task DownloadAndExtractAsync(RuntimeAsset a, string dest, string label,
                                                      IProgress<(string, double)> progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(a.Sha256))
            throw new InvalidOperationException($"{label} 파일의 SHA-256이 매니페스트에 없어서 설치하지 않아요");
        var tmp = Path.Combine(Path.GetTempPath(), "sb-" + a.Name);
        using (var resp = await Http.GetAsync(a.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(tmp);
            var buf = new byte[1 << 20];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await file.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(($"{label} 받는 중… {done / 1048576} / {total / 1048576} MB", 0.9 * done / total));
            }
        }
        progress.Report(($"{label} 확인 중…", 0.9));
        string hash;
        await using (var f = File.OpenRead(tmp)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (hash != a.Sha256.ToLowerInvariant())
        {
            File.Delete(tmp);
            throw new InvalidOperationException($"{label} 파일이 예상과 달라서(SHA-256 불일치) 설치하지 않았어요");
        }
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);
        progress.Report(($"{label} 압축 푸는 중…", 0.92));
        var (code, err) = await RunAsync("/usr/bin/tar", $"-xf \"{tmp}\" -C \"{dest}\"", null, ct);
        File.Delete(tmp);
        if (code != 0) throw new InvalidOperationException($"{label} 압축 해제 실패: {err}");
        await RunAsync("/usr/bin/xattr", $"-dr com.apple.quarantine \"{dest}\"", null, ct);
    }

    // ------------------------------------------------------------ run
    private Dictionary<string, string> Env(GraphicsBackend b)
    {
        var env = new Dictionary<string, string>
        {
            ["WINEPREFIX"] = Bottle,
            ["WINEDEBUG"] = "-all",
            ["WINEESYNC"] = "1",
            ["WINEMSYNC"] = "1",
        };
        if (b == GraphicsBackend.Dxvk && DxvkInstalled)
        {
            // DXVK-macOS builds are marked builtin: prepend their folder instead of copying into the bottle
            var dir64 = Directory.EnumerateDirectories(DxvkDir, "x64", SearchOption.AllDirectories).FirstOrDefault();
            if (dir64 != null) env["WINEDLLPATH_PREPEND"] = dir64;
            env["WINEDLLOVERRIDES"] = "d3d11,d3d10core,dxgi=b";
            env["DXVK_ASYNC"] = "1";
        }
        if (b == GraphicsBackend.D3DMetal) env["WINEDLLOVERRIDES"] = "dxgi,d3d11,d3d12=b";
        return env;
    }

    public string? WineFor(GraphicsBackend b) => b == GraphicsBackend.D3DMetal ? GptkWine : WineBinary;

    public Process Launch(WineGame g)
    {
        var wine = WineFor(g.Backend) ?? throw new InvalidOperationException(
            g.Backend == GraphicsBackend.D3DMetal ? "Game Porting Toolkit 폴더를 먼저 지정하세요" : "실행 환경을 먼저 설치하세요");
        var psi = new ProcessStartInfo(wine)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(g.ExePath) ?? Bottle,
        };
        psi.ArgumentList.Add(g.ExePath);
        foreach (var a in SplitArgs(g.Arguments)) psi.ArgumentList.Add(a);
        foreach (var kv in Env(g.Backend)) psi.Environment[kv.Key] = kv.Value;
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    /// Runs an installer (setup.exe / SteamSetup.exe) inside the bottle.
    public Task<(int, string)> RunInstallerAsync(string exe, CancellationToken ct) =>
        RunAsync(WineBinary ?? throw new InvalidOperationException("실행 환경을 먼저 설치하세요"),
                 $"\"{exe}\"", Env(GraphicsBackend.WineD3D), ct);

    public string SteamSetupUrl => "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe";

    public async Task<string> DownloadSteamSetupAsync(CancellationToken ct)
    {
        var path = Path.Combine(Root, "SteamSetup.exe");
        var bytes = await Http.GetByteArrayAsync(SteamSetupUrl, ct);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    /// Lists .exe files under the bottle's Program Files (games installed by setup/Steam).
    public IEnumerable<string> FindInstalledExes()
    {
        foreach (var pf in new[] { "Program Files", "Program Files (x86)" })
        {
            var dir = Path.Combine(Bottle, "drive_c", pf);
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { continue; }
            foreach (var f in files)
            {
                var n = Path.GetFileName(f).ToLowerInvariant();
                if (n.Contains("unins") || n.Contains("setup") || n.Contains("crash") || n.Contains("redist") || n.Contains("helper")) continue;
                yield return f;
            }
        }
    }

    private static IEnumerable<string> SplitArgs(string s) =>
        string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<(int code, string err)> RunAsync(string exe, string args, Dictionary<string, string>? env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await errTask);
    }
}
