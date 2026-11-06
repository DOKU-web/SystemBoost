// The Desktop shortcut offered in Settings, for portable copies and for people who deleted the installer's one.
// Windows: a .lnk built like the installer's, so an update keeps it working (it points at current\SystemBoost.exe).
// macOS: a symbolic link to the app; a Finder alias would ask for permission to control Finder.
// Create and Remove return null on success, else a message for the screen.
using System;
using System.IO;

namespace SystemBoost.App.Services;

public static class DesktopShortcut
{
    private static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static string LinkPath => Path.Combine(Desktop, Platform.IsMac ? "SystemBoost.app" : "SystemBoost.lnk");
    private static bool IsLink => new FileInfo(LinkPath).LinkTarget != null;

    public static bool Exists => File.Exists(LinkPath) || Directory.Exists(LinkPath) || IsLink;

    public static string? Create()
    {
        try
        {
            if (Exists) return null;
            return OperatingSystem.IsWindows() ? CreateWindowsLink() : CreateMacLink();
        }
        catch (Exception) { return "바로 가기를 만들지 못했어요. 바탕화면에 같은 이름의 항목이 있는지 확인해 주세요."; }
    }

    public static string? Remove()
    {
        try
        {
            if (Platform.IsMac && !IsLink) return "앱 복사본이라서 지우지 않았어요. 바로 가기만 지울 수 있어요.";
            File.Delete(LinkPath);
            return null;
        }
        catch (Exception) { return "바로 가기를 지우지 못했어요."; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? CreateWindowsLink()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return "앱 위치를 찾지 못했어요.";
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(LinkPath);
        link.TargetPath = exe;
        link.WorkingDirectory = Path.GetDirectoryName(exe);
        link.IconLocation = exe + ",0";
        link.Save();
        return null;
    }

    private static string? CreateMacLink()
    {
        var app = new DirectoryInfo(AppContext.BaseDirectory);
        while (app != null && app.Extension != ".app") app = app.Parent;
        if (app == null) return "앱 위치를 찾지 못했어요.";
        // macOS runs an unsigned app from a temporary folder until it is moved: a link to that folder would not last.
        if (app.FullName.Contains("/AppTranslocation/")) return "앱을 응용 프로그램 폴더로 옮긴 뒤 다시 눌러 주세요.";
        File.CreateSymbolicLink(LinkPath, app.FullName);
        return null;
    }
}
