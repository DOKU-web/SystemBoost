// PC 맞춤 추천: reads the specs on Windows, without administrator rights and without changing anything.
// Owner: the advisor part (CONTRACT.md). No WMI package: Windows' own calls, read-only.
//   CPU        the name from the registry; cores, threads and P/E cores from GetLogicalProcessorInformationEx.
//   Memory     GetPhysicallyInstalledSystemMemory; speeds and sticks from the SMBIOS table (GetSystemFirmwareTable 'RSMB').
//   GPUs       DXGI's adapters (name, vendor, own memory), matched to sb_framegen --list-gpus for the --gen-gpu value
//              and the driver's word on integrated graphics (FrameGenEngine.ListGpusAsync, which the FrameGen page uses).
//   Display    the primary display's mode and the highest refresh rate it offers at that resolution (EnumDisplaySettings).
//   Laptop     PcForm (lid, battery, platform role); on battery from GetSystemPowerStatus.
//   Windows    the registry values and power-plan checks WindowsBoostBackend reads (Game Mode, Xbox recording, HwSchMode,
//              the high-performance plan), and the driver's HAGS caps (D3DKMT, WDDM 2.7). Nothing is written.
// The byte layouts are parsed in Pure/PcSpecParse.cs, where the tests reach them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SystemBoost.App.Services.PcCheck;

[SupportedOSPlatform("windows")]
public static class PcSpecReader
{
    private const int EngineWaitMs = 8000;

    /// Unknown values stay 0, "" or null; never throws.
    public static async Task<PcSpecs> ReadAsync()
    {
        try
        {
            var specs = await Task.Run(ReadHere);
            var engine = await EngineGpusAsync();
            return specs with { Gpus = PcSpecParse.MatchEngine(specs.Gpus, engine) };
        }
        catch (Exception) { return Empty; }
    }

    private static PcSpecs Empty => new("", 0, Environment.ProcessorCount, 0, 0, 0, 0, 0, 0, Array.Empty<GpuSpec>(),
                                        new DisplaySpec(0, 0, 0, 0), false, false,
                                        new WindowsState(null, false, null, null, false, false));

    private static PcSpecs ReadHere()
    {
        var cores = Safe(ReadCores, new PcSpecParse.CpuCores(0, 0, 0, 0));
        var memory = Safe(ReadSmbiosMemory, new PcSpecParse.Memory(0, 0, 0, 0));
        long ramMB = Safe(InstalledMB, 0L);
        if (ramMB <= 0) ramMB = memory.TotalMB;
        return new PcSpecs(Safe(CpuName, ""), cores.Cores, cores.Threads > 0 ? cores.Threads : Environment.ProcessorCount,
                           cores.PCores, cores.ECores, Math.Max(0, ramMB), memory.ConfiguredMTs, memory.RatedMTs, memory.Sticks,
                           Safe(ReadGpus, (IReadOnlyList<GpuSpec>)Array.Empty<GpuSpec>()), Safe(ReadDisplay, new DisplaySpec(0, 0, 0, 0)),
                           Safe(PcForm.IsLaptop, false), Safe(OnBattery, false), Safe(ReadWindows, Empty.Windows));
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }

    // ------------------------------------------------------------ CPU
    private static string CpuName()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return k?.GetValue("ProcessorNameString") is string s ? Regex.Replace(s, @"\s+", " ").Trim() : "";
    }

    private static PcSpecParse.CpuCores ReadCores()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(0 /* RelationProcessorCore */, null, ref length);
        if (length == 0) return new PcSpecParse.CpuCores(0, 0, 0, 0);
        var buffer = new byte[length];
        if (!GetLogicalProcessorInformationEx(0, buffer, ref length)) return new PcSpecParse.CpuCores(0, 0, 0, 0);
        return PcSpecParse.ParseCores(buffer, (int)length);
    }

    // ------------------------------------------------------------ memory
    private static long InstalledMB()
    {
        if (GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0) return (long)(kb / 1024);
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)(status.TotalPhys / (1024 * 1024)) : 0;
    }

    private static PcSpecParse.Memory ReadSmbiosMemory()
    {
        const uint Rsmb = 0x52534D42;   // 'RSMB'
        uint size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size == 0 || size > 1 << 24) return new PcSpecParse.Memory(0, 0, 0, 0);
        var raw = new byte[size];
        uint got = GetSystemFirmwareTable(Rsmb, 0, raw, size);
        if (got == 0 || got > size) return new PcSpecParse.Memory(0, 0, 0, 0);
        return PcSpecParse.ParseSmbiosMemory(raw);
    }

    // ------------------------------------------------------------ GPUs
    private static IReadOnlyList<GpuSpec> ReadGpus()
    {
        var list = new List<GpuSpec>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == null) return list;
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter) < 0 || adapter == null) break;   // DXGI_ERROR_NOT_FOUND ends the list
                try
                {
                    if (adapter.GetDesc1(out var d) < 0 || (d.Flags & DxgiAdapterFlagSoftware) != 0) continue;
                    var name = (d.Description ?? "").Trim();
                    var vendor = PcSpecParse.VendorOf(d.VendorId);
                    long mb = (long)(d.DedicatedVideoMemory / (1024 * 1024));
                    list.Add(new GpuSpec(name, vendor, PcSpecParse.GuessIntegrated(vendor, name, mb), mb));
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
        }
        finally { Marshal.ReleaseComObject(factory); }
        return list;
    }

    private static async Task<IReadOnlyList<(string Value, string Name, bool Integrated)>> EngineGpusAsync()
    {
        var none = Array.Empty<(string, string, bool)>();
        try
        {
            var task = FrameGenEngine.ListGpusAsync();
            if (await Task.WhenAny(task, Task.Delay(EngineWaitMs)) != task)
            {
                _ = task.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return none;
            }
            return (await task).Where(g => g.Value != "same").Select(g => (g.Value, g.Name, g.Integrated)).ToList();
        }
        catch (Exception) { return none; }
    }

    // ------------------------------------------------------------ display
    private static DisplaySpec ReadDisplay()
    {
        string? device = null;
        var dd = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; i < 32 && EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DisplayDevicePrimary) != 0) { device = dd.DeviceName; break; }
            dd = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
        }
        var now = NewMode();
        if (!EnumDisplaySettingsW(device, EnumCurrentSettings, ref now)) return new DisplaySpec(0, 0, 0, 0);
        int w = now.PelsWidth, h = now.PelsHeight;
        double hz = now.DisplayFrequency > 1 ? now.DisplayFrequency : 0, max = hz;   // 0 and 1 mean "the hardware's default"
        for (int i = 0; i < 4096; i++)
        {
            var m = NewMode();
            if (!EnumDisplaySettingsW(device, i, ref m)) break;
            bool same = (m.PelsWidth == w && m.PelsHeight == h) || (m.PelsWidth == h && m.PelsHeight == w);   // a turned screen
            if (same && m.DisplayFrequency > max) max = m.DisplayFrequency;
        }
        return new DisplaySpec(w, h, hz, max);
    }

    private static DevMode NewMode() => new() { DeviceName = "", FormName = "", Size = (short)Marshal.SizeOf<DevMode>() };

    // ------------------------------------------------------------ power
    private static bool OnBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return false;
        return s.ACLineStatus == 0 && (s.BatteryFlag & 128) == 0;   // 128: no system battery
    }

    // ------------------------------------------------------------ Windows settings (the keys WindowsBoostBackend uses)
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string GameConfig = @"System\GameConfigStore";
    private const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    private static WindowsState ReadWindows()
    {
        var (supported, enabled) = Safe(HagsCaps, (false, (bool?)null));
        bool? hags = supported ? enabled : Safe(() => ReadDword(Registry.LocalMachine, GraphicsDrivers, "HwSchMode") switch
        {
            2 => true,
            1 => false,
            _ => (bool?)null,
        }, null);
        bool? gameMode = Safe(() =>
        {
            // Windows 10 1703 and later: on unless turned off
            var auto = ReadDword(Registry.CurrentUser, GameBar, "AutoGameModeEnabled");
            var allow = ReadDword(Registry.CurrentUser, GameBar, "AllowAutoGameMode");
            return (bool?)!(auto == 0 || allow == 0);
        }, null);
        bool? dvr = Safe(() =>
        {
            // what sys.dvr turns off: Game Bar capture (on unless set), app capture and background recording
            var enabledDvr = ReadDword(Registry.CurrentUser, GameConfig, "GameDVR_Enabled") ?? 1;
            var app = ReadDword(Registry.CurrentUser, GameDvr, "AppCaptureEnabled") ?? 0;
            var history = ReadDword(Registry.CurrentUser, GameDvr, "HistoricalCaptureEnabled") ?? 0;
            return (bool?)(enabledDvr != 0 || app != 0 || history != 0);
        }, null);
        bool active = Safe(() => ActivePlan() is Guid g && IsHighPerformance(g), false);
        bool exists = Safe(() => FindHighPlan() != null, false);
        return new WindowsState(hags, supported, gameMode, dvr, active, exists);
    }

    private static int? ReadDword(RegistryKey root, string key, string name)
    {
        using var k = root.OpenSubKey(key);
        return k?.GetValue(name) is int v ? v : null;
    }

    private static readonly Guid HighPlan = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePlan = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly Guid PersonalitySetting = new("245d8541-3943-4422-b025-13a784f679b7");

    private static Guid? ActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    // the same test as WindowsBoostBackend.IsHighPerformance: the High or Ultimate plan, or a plan built on High
    private static bool IsHighPerformance(Guid scheme)
    {
        if (scheme == HighPlan || scheme == UltimatePlan) return true;
        var buffer = new byte[16];
        uint size = 16;
        var sub = NoSubgroup;
        var setting = PersonalitySetting;
        return PowerReadACValue(IntPtr.Zero, ref scheme, ref sub, ref setting, out _, buffer, ref size) == 0 && size == 16 && new Guid(buffer) == HighPlan;
    }

    private static Guid? FindHighPlan()
    {
        var plans = new List<Guid>();
        var buffer = new byte[16];
        for (uint i = 0; i < 256; i++)
        {
            uint size = 16;
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, i, buffer, ref size) != 0) break;
            plans.Add(new Guid(buffer));
        }
        if (plans.Contains(UltimatePlan)) return UltimatePlan;
        if (plans.Contains(HighPlan)) return HighPlan;
        return plans.Where(IsHighPerformance).Cast<Guid?>().FirstOrDefault();
    }

    /// Whether a GPU driver offers hardware-accelerated GPU scheduling and has it on (D3DKMT_WDDM_2_7_CAPS).
    private static (bool Supported, bool? Enabled) HagsCaps()
    {
        var e = new D3dkmtEnumAdapters2();
        D3DKMTEnumAdapters2(ref e);   // without a buffer: only the count
        if (e.NumAdapters == 0) return (false, null);
        int count = (int)Math.Min(e.NumAdapters, 64u), item = Marshal.SizeOf<D3dkmtAdapterInfo>();
        var mem = Marshal.AllocHGlobal(count * item);
        var caps = Marshal.AllocHGlobal(4);
        try
        {
            e.NumAdapters = (uint)count;
            e.Adapters = mem;
            if (D3DKMTEnumAdapters2(ref e) != 0) return (false, null);
            bool supported = false, enabled = false;
            for (int i = 0; i < (int)Math.Min(e.NumAdapters, (uint)count); i++)
            {
                var a = Marshal.PtrToStructure<D3dkmtAdapterInfo>(mem + i * item);
                Marshal.WriteInt32(caps, 0);
                var q = new D3dkmtQueryAdapterInfo { Adapter = a.Adapter, Type = KmtqaiTypeWddm27Caps, PrivateData = caps, PrivateDataSize = 4 };
                if (D3DKMTQueryAdapterInfo(ref q) == 0)
                {
                    int bits = Marshal.ReadInt32(caps);
                    if ((bits & 1) != 0) { supported = true; enabled |= (bits & 2) != 0; }   // HwSchSupported, HwSchEnabled
                }
                var close = new D3dkmtCloseAdapter { Adapter = a.Adapter };
                D3DKMTCloseAdapter(ref close);
            }
            return supported ? (true, enabled) : (false, null);
        }
        finally { Marshal.FreeHGlobal(mem); Marshal.FreeHGlobal(caps); }
    }

    // ------------------------------------------------------------ Win32
    private const uint DxgiAdapterFlagSoftware = 2;
    private const int EnumCurrentSettings = -1;
    private const int DisplayDevicePrimary = 0x4;
    private const int KmtqaiTypeWddm27Caps = 70;

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent (not called)
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        // IDXGIFactory: EnumAdapters, MakeWindowAssociation, GetWindowAssociation, CreateSwapChain, CreateSoftwareAdapter (not called)
        [PreserveSig] int EnumAdapters();
        [PreserveSig] int MakeWindowAssociation();
        [PreserveSig] int GetWindowAssociation();
        [PreserveSig] int CreateSwapChain();
        [PreserveSig] int CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject (not called)
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        // IDXGIAdapter: EnumOutputs, GetDesc, CheckInterfaceSupport (not called)
        [PreserveSig] int EnumOutputs();
        [PreserveSig] int GetDesc();
        [PreserveSig] int CheckInterfaceSupport();
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtAdapterInfo
    {
        public uint Adapter, LuidLow;
        public int LuidHigh;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtEnumAdapters2
    {
        public uint NumAdapters;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtQueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr PrivateData;
        public uint PrivateDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtCloseAdapter
    {
        public uint Adapter;
    }

    [DllImport("kernel32", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(int relationship, byte[]? buffer, ref uint length);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    [DllImport("kernel32", SetLastError = true)] private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("kernel32")] private static extern IntPtr LocalFree(IntPtr p);
    [DllImport("dxgi", ExactSpelling = true)] private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DevMode info);
    [DllImport("gdi32", ExactSpelling = true)] private static extern int D3DKMTEnumAdapters2(ref D3dkmtEnumAdapters2 info);
    [DllImport("gdi32", ExactSpelling = true)] private static extern int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo info);
    [DllImport("gdi32", ExactSpelling = true)] private static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter info);
    [DllImport("powrprof")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint access, uint index, byte[] buffer, ref uint size);
    [DllImport("powrprof")] private static extern uint PowerReadACValue(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint type, byte[] buffer, ref uint size);
}
