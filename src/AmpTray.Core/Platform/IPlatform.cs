using System;
using System.Collections.Generic;

namespace AmpTray.Platform;

/// <summary>
/// The operating-system services the core needs. Each front end installs its implementation in
/// <see cref="PlatformServices.Current"/> before anything else runs; the core never calls an
/// OS-specific API directly.
/// </summary>
public interface IPlatform
{
    /// <summary>
    /// Serial port names (as <c>SerialPort</c> opens them) whose USB device has this vendor and
    /// product ID. May include ports that are not attached right now; the caller filters those out.
    /// </summary>
    IReadOnlyCollection<string> FindUsbSerialPorts(ushort vendorId, ushort productId);

    /// <summary>
    /// The cross-process lock other sensor tools take around their own access to the device, or
    /// null when there is none or it cannot be opened (then <paramref name="problem"/> says why,
    /// if it is worth telling the user).
    /// </summary>
    IDeviceLock? OpenDeviceLock(out string? problem);

    /// <summary>
    /// Starts a forced power-off, after <paramref name="delaySeconds"/> when that is above zero.
    /// Returns false (with the reason passed to <paramref name="onFailure"/>) when it could not be
    /// started; a refusal reported later by the OS also goes to <paramref name="onFailure"/>.
    /// </summary>
    bool StartShutdown(int delaySeconds, string comment, Action<string> onFailure);

    /// <summary>Calls off a delayed shutdown started by <see cref="StartShutdown"/>. Never throws.</summary>
    void AbortShutdown();

    /// <summary>The per-process frame counter the background monitor runs, or one that never starts.</summary>
    IFpsSource CreateFpsSource();

    /// <summary>Collects garbage and hands unused pages back to the OS. Never throws.</summary>
    void TrimMemory();
}

/// <summary>A held-across-processes lock around one exchange with the device.</summary>
public interface IDeviceLock : IDisposable
{
    /// <summary>Waits for the lock; true once this thread owns it.</summary>
    bool Wait(int timeoutMs);

    /// <summary>Releases a lock this thread acquired with <see cref="Wait"/>.</summary>
    void Release();
}

/// <summary>Counts presented frames per process and publishes them to <c>fps.json</c>.</summary>
public interface IFpsSource : IDisposable
{
    bool Active { get; }
    void SetInterval(int ms);
    void Start();
}
