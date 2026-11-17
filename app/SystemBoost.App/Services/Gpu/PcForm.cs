// Laptop or desktop, read from Windows' power information (no extra program, no WMI package):
//   a lid, a system battery that is not a UPS (BatteriesAreShortTerm), or a mobile or tablet platform role
//   (which Windows takes from the firmware's chassis setting) count as a laptop.
// GPU tuning is for desktop graphics cards only: a laptop maker sets power and clocks for its case and cooling.
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SystemBoost.App.Services;

[SupportedOSPlatform("windows")]
internal static class PcForm
{
    private const uint PlatformRoleV2 = 2;
    private const int Mobile = 2, Slate = 8;
    // SYSTEM_POWER_CAPABILITIES (76 bytes): LidPresent at byte 2, SystemBatteriesPresent at 30, BatteriesAreShortTerm at 31
    private const int LidPresent = 2, BatteriesPresent = 30, ShortTerm = 31;

    private static readonly Lazy<bool> Laptop = new(Read);

    public static bool IsLaptop() => Laptop.Value;

    private static bool Read()
    {
        try
        {
            int role = PowerDeterminePlatformRoleEx(PlatformRoleV2);
            if (role is Mobile or Slate) return true;
            var caps = new byte[128];
            if (!GetPwrCapabilities(caps)) return true;   // cannot tell: the safe side
            return caps[LidPresent] != 0 || (caps[BatteriesPresent] != 0 && caps[ShortTerm] == 0);
        }
        catch (Exception) { return true; }   // cannot tell: no tuning, which is the safe side
    }

    [DllImport("powrprof")] private static extern int PowerDeterminePlatformRoleEx(uint version);
    [DllImport("powrprof")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool GetPwrCapabilities([Out] byte[] capabilities);
}
