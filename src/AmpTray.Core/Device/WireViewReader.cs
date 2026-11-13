using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using AmpTray.Platform;

namespace AmpTray.Device;

/// <summary>
/// Client for the Thermal Grizzly WireView Pro II that reads, and can do exactly one other thing.
///
/// The only traffic this class ever produces is:
///   * the identification handshake (raise RTS, device answers with its welcome string),
///   * CMD_READ_SENSOR_VALUES (0x04), which returns a 100-byte sensor struct, and
///   * for <see cref="ClearFaults"/>, when the user asks for it and never otherwise: a read of the
///     device's settings (CMD_READ_CONFIG, 0x05), CMD_CLEAR_FAULTS (0x0E) and a redraw of the
///     device's current screen (0x0C 0xEF).
/// There is deliberately no general "send" method, so config writes, calibration, flash, bootloader
/// and any other screen command cannot be issued from here.
///
/// The port is opened per request under the platform's shared device lock (on Windows the
/// "Global\Access_USB_Sensors" mutex the official app and other sensor tools use), so they can all
/// run side by side.
/// </summary>
public sealed class WireViewReader : IDisposable
{
    private const string WelcomeMessage = "Thermal Grizzly WireView Pro II";
    private const ushort UsbVendorId = 0x0483;
    private const ushort UsbProductId = 0x5740;
    internal const int SensorStructSize = 100;
    private const int IoTimeoutMs = 1000;

    private static readonly byte[] CmdReadSensorValues = { 0x04 };

    private static readonly byte[] CmdReadConfig = { 0x05 };
    private const byte CmdClearFaults = 0x0E;

    /// <summary>The six fault bits the firmware defines.</summary>
    internal const ushort KnownFaults = 0x3F;

    /// <summary>
    /// Bits set in every keep-mask besides the faults to keep: the undefined bit 6 and the whole high
    /// byte. The firmware only ANDs, so keeping bits that are never set changes nothing, and it means
    /// no mask byte can coincide with a known command opcode (0x40..0x7F and 0xFF hold none) should
    /// a firmware ever read the mask bytes as commands.
    /// </summary>
    internal const ushort KeepFiller = 0xFF40;

    /// <summary>
    /// CMD_SCREEN_CHANGE with SCREEN_GOTO_SAME: stay on the current screen and draw it again. The
    /// device's own display keeps showing a fault after its bits are cleared until it is redrawn,
    /// which is why the official app sends this right after a clear. No reply.
    /// </summary>
    internal static readonly byte[] CmdRedrawScreen = { 0x0C, 0xEF };

    private IDeviceLock? _lock;
    private bool _lockTried;
    private bool _resync;

    // VID_0483/PID_5740 is ST's generic virtual COM port ID, shared by many hobby boards. Ports that
    // failed identification are skipped for a while (or until the set of ports changes), then tried
    // again: after sleep or a USB hiccup the WireView itself can fail one handshake.
    private static readonly TimeSpan RejectFor = TimeSpan.FromSeconds(20);
    private readonly Dictionary<string, DateTime> _rejected = new(StringComparer.OrdinalIgnoreCase);
    private string _portSetKey = "";

    /// <summary>Retry every port on the next scan (called on resume from sleep).</summary>
    public void ForgetRejections()
    {
        lock (_rejected) _rejected.Clear();
    }

    public string? PortName { get; private set; }

    /// <summary>Set when the shared mutex could not be created or opened; polling then runs without it.</summary>
    public string? MutexProblem { get; private set; }

    /// <summary>Finds and identifies the device. Returns false if none is attached.</summary>
    public bool TryConnect()
    {
        PortName = null;
        foreach (var port in FindCandidatePorts())
        {
            lock (_rejected)
            {
                if (_rejected.TryGetValue(port, out var until) && DateTime.UtcNow < until) continue;
            }
            try
            {
                if (Identify(port))
                {
                    PortName = port;
                    ForgetRejections();
                    return true;
                }
                lock (_rejected) _rejected[port] = DateTime.UtcNow + RejectFor;
            }
            catch
            {
                // Port busy; try again next scan.
            }
        }
        return false;
    }

    /// <summary>Reads one sensor sample. Returns null on timeout, an implausible frame, or if the port is unavailable.</summary>
    public SensorSnapshot? ReadSensors()
    {
        if (PortName == null) return null;
        var raw = Exchange(PortName, port =>
        {
            port.Write(CmdReadSensorValues, 0, CmdReadSensorValues.Length);
            return ReadExact(port, SensorStructSize);
        });
        if (raw == null || !IsPlausible(raw))
        {
            // Partial or misaligned frame: flush whatever is still in flight before the next command.
            _resync = true;
            return null;
        }
        return Parse(raw);
    }

    /// <summary>
    /// Clears the device's faults, and only ever that: it must not get in the way of the device's
    /// own protection.
    ///
    /// The device can be set to power the PC off for a fault (its soft/hard power actions). What a
    /// clear does to such a shutdown once it is counting down is not documented, so none is sent
    /// while one of those faults is active: the device's settings and a fresh reading are taken
    /// first, and if any fault with a power action is active nothing goes out at all. Otherwise the
    /// active and logged faults are cleared and the screen is redrawn. If the settings cannot be
    /// read, nothing is sent either.
    /// </summary>
    public ClearFaultsResult ClearFaults()
    {
        string? portName = PortName;
        if (portName == null) return new(ClearFaultsOutcome.NotSent, 0);
        try
        {
            var plan = PlanClear(portName);
            if (plan.Frame == null) return plan.Stopped;

            // Separate exchanges, the way the official app does it.
            bool Send(byte[] bytes) => Exchange(portName, port =>
            {
                port.Write(bytes, 0, bytes.Length);
                return bytes;
            }) != null;

            if (!Send(plan.Frame)) return new(ClearFaultsOutcome.NotSent, 0);
            // The redraw is a courtesy: if it fails the faults are cleared all the same.
            try { Send(CmdRedrawScreen); }
            catch { /* port grabbed or unplugged between the two requests */ }
            return new(ClearFaultsOutcome.Cleared, 0);
        }
        finally
        {
            // Nothing should be left in flight, but make the next read start from a clean line.
            _resync = true;
        }
    }

    /// <summary>
    /// The reading half of a clear: the device's settings and a fresh sample, and from them either the
    /// frame to send or the reason to send nothing. Changes nothing on the device.
    /// </summary>
    private (byte[]? Frame, ClearFaultsResult Stopped, ushort PowerOffFaults) PlanClear(string portName)
    {
        var config = Exchange(portName, port =>
        {
            port.Write(CmdReadConfig, 0, CmdReadConfig.Length);
            return ReadUntilQuiet(port);
        });
        if (config == null) return (null, new(ClearFaultsOutcome.NotSent, 0), 0);
        if (ParsePowerOffFaults(config) is not ushort powerOffFaults)
            return (null, new(ClearFaultsOutcome.SettingsUnreadable, 0), 0);

        // A fresh reading, not the one the button was pressed on.
        var now = ReadSensors();
        if (now == null) return (null, new(ClearFaultsOutcome.NotSent, 0), powerOffFaults);

        var frame = BuildClearFrame(powerOffFaults, now.FaultStatus, now.FaultLog, out ushort blockedBy);
        var stopped = new ClearFaultsResult(blockedBy != 0 ? ClearFaultsOutcome.LeftAlone : ClearFaultsOutcome.NothingToClear, blockedBy);
        return (frame, stopped, powerOffFaults);
    }

    /// <summary>
    /// The faults the device is set to power the PC off for (soft or hard), from a CMD_READ_CONFIG
    /// reply. The three known layouts are 72, 74 and 96 bytes long (version byte 0, 1, 2) and share
    /// these fields: 2 version, 50 ushort soft-power faults, 52 ushort hard-power faults.
    /// Null for anything else, so an unknown layout is never guessed at.
    /// </summary>
    internal static ushort? ParsePowerOffFaults(byte[] config)
    {
        if (config.Length < 54) return null;
        int expected = config[2] switch { 0 => 72, 1 => 74, 2 => 96, _ => -1 };
        if (config.Length != expected) return null;
        ushort soft = BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(50));
        ushort hard = BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(52));
        if (((soft | hard) & ~KnownFaults) != 0) return null;
        return (ushort)(soft | hard);
    }

    /// <summary>
    /// CMD_CLEAR_FAULTS followed by two little-endian keep-masks, one for the active faults
    /// (FaultStatus) and one for the latched ones (FaultLog): the firmware does "faults &amp;= mask",
    /// so a set bit keeps that fault (all ones changes nothing, zero clears everything). No reply.
    ///
    /// Returns null, so that nothing is sent, when a fault the device powers the PC off for is active
    /// (<paramref name="blockedBy"/> says which) or when there is nothing to clear. Otherwise the
    /// log is cleared entirely, and the active mask still keeps every power-off fault: none is active
    /// in this reading, but one raised between the reading and the clear must survive it.
    /// </summary>
    internal static byte[]? BuildClearFrame(ushort powerOffFaults, ushort status, ushort log, out ushort blockedBy)
    {
        powerOffFaults &= KnownFaults;
        blockedBy = (ushort)(status & powerOffFaults);
        if (blockedBy != 0 || (status == 0 && log == 0)) return null;

        ushort keepStatus = (ushort)(powerOffFaults | KeepFiller);
        ushort keepLog = KeepFiller;
        return new[]
        {
            CmdClearFaults,
            (byte)(keepStatus & 0xFF), (byte)(keepStatus >> 8),
            (byte)(keepLog & 0xFF), (byte)(keepLog >> 8),
        };
    }

    private bool Identify(string portName)
    {
        int len = WelcomeMessage.Length + 1;
        var raw = Exchange(portName, port =>
        {
            port.RtsEnable = true;
            try { return ReadExact(port, len); }
            finally { port.RtsEnable = false; }
        });
        return raw != null && Encoding.ASCII.GetString(raw).TrimEnd('\0') == WelcomeMessage;
    }

    /// <summary>
    /// SensorStruct layout (Pack = 4):
    ///   0  short[4] Ts (0.1 °C: In, Out, TS1, TS2)
    ///   8  ushort Vdd, 10 byte FanDuty, 11 pad
    ///   12 PowerSensor[6] { short mV, pad, uint mA, uint mW } (12 bytes each)
    ///   84 uint TotalPower, 88 uint TotalCurrent, 92 ushort AvgVoltage, 94 byte HpwrCap, 95 pad
    ///   96 ushort FaultStatus, 98 ushort FaultLog
    /// </summary>
    internal static SensorSnapshot Parse(byte[] b)
    {
        ReadOnlySpan<byte> s = b;
        var volts = new double[SensorSnapshot.PinCount];
        var amps = new double[SensorSnapshot.PinCount];
        for (int i = 0; i < SensorSnapshot.PinCount; i++)
        {
            int o = 12 + i * 12;
            volts[i] = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o)) / 1000.0;
            amps[i] = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o + 4)) / 1000.0;
        }
        return new SensorSnapshot
        {
            TempInC = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(0)) / 10.0,
            TempOutC = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(2)) / 10.0,
            TempTs1C = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(4)) / 10.0,
            TempTs2C = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(6)) / 10.0,
            PinVoltage = volts,
            PinCurrent = amps,
            CableRatingW = b[94] switch { 0 => 600, 1 => 450, 2 => 300, 3 => 150, _ => 0 },
            FaultStatus = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(96)),
            FaultLog = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(98)),
        };
    }

    /// <summary>
    /// Rejects frames that cannot be a real sensor struct (e.g. after a partial read left bytes in
    /// flight and the next frame arrived misaligned). The firmware's own totals must agree with the
    /// per-wire readings, and every field must be in a physically possible range.
    /// </summary>
    internal static bool IsPlausible(byte[] b)
    {
        if (b.Length != SensorStructSize) return false;
        ReadOnlySpan<byte> s = b;

        for (int i = 0; i < 4; i++)
        {
            short t = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(i * 2));
            if (t < -1000 || t > 2000) return false; // -100 .. +200 °C
        }

        long sumMa = 0, sumMw = 0;
        for (int i = 0; i < SensorSnapshot.PinCount; i++)
        {
            int o = 12 + i * 12;
            int mv = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o));
            uint ma = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o + 4));
            uint mw = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o + 8));
            if (mv < -1000 || mv > 20000 || ma > 30000 || mw > 600000) return false;
            // Firmware power must match V×I (tolerance for rounding and averaging windows).
            double expected = (double)mv * ma / 1000.0;
            if (Math.Abs(mw - expected) > Math.Max(500, expected * 0.10)) return false;
            sumMa += ma;
            sumMw += mw;
        }

        uint totalMw = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(84));
        uint totalMa = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(88));
        if (Math.Abs((long)totalMa - sumMa) > Math.Max(200, sumMa * 0.05)) return false;
        if (Math.Abs((long)totalMw - sumMw) > Math.Max(2000, sumMw * 0.05)) return false;

        if (b[94] > 3) return false; // cable rating enum
        ushort status = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(96));
        ushort log = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(98));
        return (status & ~0x3F) == 0 && (log & ~0x3F) == 0; // six known fault bits
    }

    /// <summary>Opens the port under the shared lock (when available), runs one exchange, then closes and releases.</summary>
    private byte[]? Exchange(string portName, Func<SerialPort, byte[]?> body)
    {
        var shared = GetLock();
        bool owned = false;
        if (shared != null)
        {
            owned = shared.Wait(2000);
            if (!owned) return null;
        }

        try
        {
            using var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = IoTimeoutMs,
                WriteTimeout = IoTimeoutMs,
            };
            port.Open();
            if (_resync)
            {
                // Give any stale bytes from an interrupted frame time to land, then drop them.
                Thread.Sleep(60);
                _resync = false;
            }
            port.DiscardInBuffer();
            return body(port);
        }
        finally
        {
            if (owned) shared!.Release();
        }
    }

    /// <summary>Opens the shared lock once; if the platform cannot provide it, polling runs without it.</summary>
    private IDeviceLock? GetLock()
    {
        if (_lockTried) return _lock;
        _lockTried = true;
        _lock = PlatformServices.Current.OpenDeviceLock(out var problem);
        MutexProblem = problem;
        return _lock;
    }

    private static byte[]? ReadExact(SerialPort port, int size)
    {
        var buf = new byte[size];
        int got = 0;
        long start = Environment.TickCount64;
        while (got < size && Environment.TickCount64 - start < IoTimeoutMs)
        {
            if (port.BytesToRead > 0)
                got += port.Read(buf, got, size - got);
            else
                Thread.Sleep(2);
        }
        return got == size ? buf : null;
    }

    /// <summary>
    /// Reads a reply whose length depends on the firmware: everything that arrives until the line has
    /// been quiet for a moment, so nothing is left in flight for the next request.
    /// </summary>
    private static byte[] ReadUntilQuiet(SerialPort port)
    {
        const int QuietMs = 150, MaxBytes = 512;
        var buf = new byte[MaxBytes];
        int got = 0;
        long start = Environment.TickCount64, last = start;
        while (got < MaxBytes && Environment.TickCount64 - last < QuietMs && Environment.TickCount64 - start < IoTimeoutMs * 2)
        {
            if (port.BytesToRead > 0)
            {
                got += port.Read(buf, got, MaxBytes - got);
                last = Environment.TickCount64;
            }
            else
            {
                Thread.Sleep(2);
            }
        }
        return buf.AsSpan(0, got).ToArray();
    }

    /// <summary>Serial ports belonging to the STM32 virtual COM port VID/PID, present right now.</summary>
    private IEnumerable<string> FindCandidatePorts()
    {
        var present = SerialPort.GetPortNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        string key = string.Join("|", present);
        if (key != _portSetKey)
        {
            _portSetKey = key;
            ForgetRejections(); // something was plugged or unplugged: re-check everything
        }
        var presentSet = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);

        return PlatformServices.Current.FindUsbSerialPorts(UsbVendorId, UsbProductId)
            .Where(presentSet.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Dispose() => _lock?.Dispose();
}
