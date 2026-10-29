using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AmpTray.Gpu;

/// <summary>
/// Minimal P/Invoke surface over NVIDIA's NVML (nvml.dll on Windows, libnvidia-ml.so.1 on Linux;
/// both ship with the display driver). Every call here is a read; NVML's write/control APIs are
/// deliberately not declared.
/// </summary>
internal static class Nvml
{
    private const string Lib = "nvml.dll";
    private static readonly string[] LinuxLibs = { "libnvidia-ml.so.1", "libnvidia-ml.so" };

    /// <summary>The library name as the user would know it on this OS, for messages.</summary>
    public static string LibraryName => OperatingSystem.IsWindows() ? Lib : LinuxLibs[0];

    static Nvml()
    {
        if (OperatingSystem.IsWindows()) return;
        NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, Resolve);
    }

    /// <summary>Only the NVML import is redirected; anything else keeps the default probing.</summary>
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Lib) return IntPtr.Zero;
        foreach (var lib in LinuxLibs)
            if (NativeLibrary.TryLoad(lib, assembly, searchPath, out var handle)) return handle;
        throw new DllNotFoundException($"{LinuxLibs[0]} not found");
    }

    public const int Success = 0;
    public const int NotSupported = 3;

    public const int TempGpu = 0;
    public const int ThresholdShutdown = 0, ThresholdSlowdown = 1;
    public const int ClockGraphics = 0, ClockSm = 1, ClockMem = 2;
    public const int PcieTx = 0, PcieRx = 1;
    public const uint FieldMemoryTemp = 180; // NVML_FI_DEV_MEMORY_TEMP

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization { public uint Gpu, Memory; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory { public ulong Total, Free, Used; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FieldValue
    {
        public uint FieldId;
        public uint ScopeId;
        public long Timestamp;
        public long LatencyUsec;
        public int ValueType;   // 0 double, 1 uint, 2 ulong, 3 ulonglong, 4 longlong, 5 int
        public int Result;      // nvmlReturn_t for this field
        public ulong Value;     // union storage
    }

    [DllImport(Lib, EntryPoint = "nvmlInit_v2")] public static extern int Init();
    [DllImport(Lib, EntryPoint = "nvmlShutdown")] public static extern int Shutdown();
    [DllImport(Lib, EntryPoint = "nvmlErrorString")] public static extern IntPtr ErrorString(int result);
    [DllImport(Lib, EntryPoint = "nvmlSystemGetDriverVersion")] public static extern int GetDriverVersion(byte[] buffer, uint length);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetCount_v2")] public static extern int GetCount(out uint count);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] public static extern int GetHandleByIndex(uint index, out IntPtr device);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetName")] public static extern int GetName(IntPtr device, byte[] buffer, uint length);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetTemperature")] public static extern int GetTemperature(IntPtr device, int sensor, out uint celsius);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetTemperatureThreshold")] public static extern int GetTemperatureThreshold(IntPtr device, int threshold, out uint celsius);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetFieldValues")] public static extern int GetFieldValues(IntPtr device, int count, [In, Out] FieldValue[] values);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetUtilizationRates")] public static extern int GetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetEncoderUtilization")] public static extern int GetEncoderUtilization(IntPtr device, out uint percent, out uint samplingUs);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetDecoderUtilization")] public static extern int GetDecoderUtilization(IntPtr device, out uint percent, out uint samplingUs);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetMemoryInfo")] public static extern int GetMemoryInfo(IntPtr device, out Memory memory);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerUsage")] public static extern int GetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] public static extern int GetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")] public static extern int GetPowerLimitConstraints(IntPtr device, out uint minMw, out uint maxMw);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetTotalEnergyConsumption")] public static extern int GetTotalEnergy(IntPtr device, out ulong millijoules);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetClockInfo")] public static extern int GetClockInfo(IntPtr device, int clock, out uint mhz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetMaxClockInfo")] public static extern int GetMaxClockInfo(IntPtr device, int clock, out uint mhz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPerformanceState")] public static extern int GetPerformanceState(IntPtr device, out int pstate);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")] public static extern int GetClocksEventReasons(IntPtr device, out ulong reasons);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] public static extern int GetClocksThrottleReasons(IntPtr device, out ulong reasons);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetNumFans")] public static extern int GetNumFans(IntPtr device, out uint count);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetFanSpeed_v2")] public static extern int GetFanSpeed(IntPtr device, uint fan, out uint percent);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetFanSpeed")] public static extern int GetFanSpeedLegacy(IntPtr device, out uint percent);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetCurrPcieLinkGeneration")] public static extern int GetPcieGeneration(IntPtr device, out uint generation);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetCurrPcieLinkWidth")] public static extern int GetPcieWidth(IntPtr device, out uint lanes);

    public static string Error(int code)
    {
        try { return Marshal.PtrToStringAnsi(ErrorString(code)) ?? $"NVML error {code}"; }
        catch { return $"NVML error {code}"; }
    }

    public static string Ansi(byte[] buffer)
    {
        int n = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, n < 0 ? buffer.Length : n);
    }
}
