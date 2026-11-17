// AMD Software: Adrenalin Edition, where the user turns AMD's own features (Fluid Motion Frames, Chill, ...) on and off.
// SystemBoost never turns them off itself; it only opens the program when it is installed.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace SystemBoost.App.Services;

[SupportedOSPlatform("windows")]
internal static class AmdSoftware
{
    private static readonly string[] Programs =
    {
        @"AMD\CNext\CNext\RadeonSoftware.exe",   // Adrenalin 2020 and later
        @"AMD\CNext\CNext\RadeonSettings.exe",   // the 2019 name
    };

    /// The program's path, null when it is not installed.
    public static string? Find()
    {
        var roots = new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
            .Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            foreach (var p in Programs)
            {
                var path = Path.Combine(root!, p);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    /// False when it could not be started.
    public static bool Open()
    {
        if (Find() is not { } path) return false;
        try { using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return true; }
        catch (Exception) { return false; }
    }
}
