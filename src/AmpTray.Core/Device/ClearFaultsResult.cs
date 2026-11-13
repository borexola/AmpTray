namespace AmpTray.Device;

/// <summary>How a request to clear the device's faults ended.</summary>
public enum ClearFaultsOutcome
{
    /// <summary>Nothing reached the device (unplugged, port busy, no reading).</summary>
    NotSent,
    /// <summary>The device's settings could not be read, so no clear was sent.</summary>
    SettingsUnreadable,
    /// <summary>A fault the device powers the PC off for is active, so no clear was sent.</summary>
    LeftAlone,
    /// <summary>The device reported no fault by the time the request ran.</summary>
    NothingToClear,
    /// <summary>The clear went out; the next sensor sample shows what the device did with it.</summary>
    Cleared,
}

/// <param name="Outcome">How the request ended.</param>
/// <param name="BlockedBy">For <see cref="ClearFaultsOutcome.LeftAlone"/>: the active faults the device is set to power the PC off for.</param>
public readonly record struct ClearFaultsResult(ClearFaultsOutcome Outcome, ushort BlockedBy);
