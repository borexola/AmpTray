using System;
using System.Collections.Generic;

namespace AmpTray.Platform;

/// <summary>Holds the platform implementation the running front end installed.</summary>
public static class PlatformServices
{
    /// <summary>Defaults to <see cref="NullPlatform"/>, which does nothing; tests can swap in a fake.</summary>
    public static IPlatform Current { get; set; } = new NullPlatform();
}

/// <summary>
/// A platform with no OS integration: no device ports, no shared lock, no shutdown and no FPS.
/// Used until a front end installs the real one, and where a feature has no implementation yet.
/// </summary>
public sealed class NullPlatform : IPlatform
{
    public IReadOnlyCollection<string> FindUsbSerialPorts(ushort vendorId, ushort productId) => Array.Empty<string>();

    public IDeviceLock? OpenDeviceLock(out string? problem)
    {
        problem = null;
        return null;
    }

    public bool StartShutdown(int delaySeconds, string comment, Action<string> onFailure)
    {
        onFailure("Shutdown is not supported on this platform.");
        return false;
    }

    public void AbortShutdown() { }

    public IFpsSource CreateFpsSource() => new NoFpsSource();

    public void TrimMemory()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch
        {
            // Purely cosmetic.
        }
    }

}

/// <summary>An FPS source that never starts, for platforms without a frame counter.</summary>
public sealed class NoFpsSource : IFpsSource
{
    public bool Active => false;
    public void SetInterval(int ms) { }
    public void Start() { }
    public void Dispose() { }
}
