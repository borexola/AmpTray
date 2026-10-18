using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AmpTray.Device;

/// <summary>
/// Strictly read-only client for the Thermal Grizzly WireView Pro II.
///
/// The only traffic this class ever produces is:
///   * the identification handshake (raise RTS, device answers with its welcome string), and
///   * CMD_READ_SENSOR_VALUES (0x04), which returns a 100-byte sensor struct.
/// There is deliberately no general "send" method, so config, calibration, flash, screen,
/// fault-clear and bootloader commands cannot be issued from here.
///
/// The port is opened per request under the "Global\Access_USB_Sensors" mutex, the same
/// convention the official app (and other sensor tools) use, so they can all run side by side.
/// </summary>
public sealed class WireViewReader : IDisposable
{
    private const string WelcomeMessage = "Thermal Grizzly WireView Pro II";
    private const string SharedMutexName = @"Global\Access_USB_Sensors";
    private const string UsbVidPid = "VID_0483&PID_5740";
    private const int SensorStructSize = 100;
    private const int IoTimeoutMs = 1000;

    private static readonly byte[] CmdReadSensorValues = { 0x04 };

    private Mutex? _mutex;
    private bool _mutexTried;
    private bool _resync;

    // VID_0483/PID_5740 is ST's generic virtual COM port ID, shared by many hobby boards. Ports that
    // failed identification are skipped until the set of ports on the machine changes.
    private readonly HashSet<string> _rejected = new(StringComparer.OrdinalIgnoreCase);
    private string _portSetKey = "";

    public string? PortName { get; private set; }

    /// <summary>Set when the shared mutex could not be created or opened; polling then runs without it.</summary>
    public string? MutexProblem { get; private set; }

    /// <summary>Finds and identifies the device. Returns false if none is attached.</summary>
    public bool TryConnect()
    {
        PortName = null;
        foreach (var port in FindCandidatePorts())
        {
            if (_rejected.Contains(port)) continue;
            try
            {
                if (Identify(port))
                {
                    PortName = port;
                    _rejected.Clear();
                    return true;
                }
                _rejected.Add(port);
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
    private static SensorSnapshot Parse(byte[] b)
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

    /// <summary>Opens the port under the shared mutex (when available), runs one exchange, then closes and releases.</summary>
    private byte[]? Exchange(string portName, Func<SerialPort, byte[]?> body)
    {
        var mutex = GetMutex();
        bool owned = false;
        if (mutex != null)
        {
            try { owned = mutex.WaitOne(2000); }
            catch (AbandonedMutexException) { owned = true; }
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
            if (owned) mutex!.ReleaseMutex();
        }
    }

    /// <summary>
    /// The shared mutex may already exist with an ACL we cannot create against (a tool running
    /// elevated made it). Try to open it instead; if that fails too, run without it rather than dying.
    /// </summary>
    private Mutex? GetMutex()
    {
        if (_mutexTried) return _mutex;
        _mutexTried = true;
        try
        {
            _mutex = new Mutex(false, SharedMutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
        {
            try { _mutex = Mutex.OpenExisting(SharedMutexName); }
            catch (Exception ex2) { MutexProblem = ex2.Message; _mutex = null; }
        }
        catch (Exception ex)
        {
            MutexProblem = ex.Message;
            _mutex = null;
        }
        return _mutex;
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

    /// <summary>COM ports belonging to the STM32 virtual COM port VID/PID, present right now.</summary>
    private IEnumerable<string> FindCandidatePorts()
    {
        var present = SerialPort.GetPortNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        string key = string.Join("|", present);
        if (key != _portSetKey)
        {
            _portSetKey = key;
            _rejected.Clear(); // something was plugged or unplugged: re-check everything
        }
        var presentSet = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);

        var found = new List<string>();
        // Match "VID_0483&PID_5740" and variants such as "...&MI_00" (composite) or "...&REV_0200".
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb != null)
        {
            foreach (var hwId in usb.GetSubKeyNames().Where(k => k.StartsWith(UsbVidPid, StringComparison.OrdinalIgnoreCase)))
            {
                using var dev = usb.OpenSubKey(hwId);
                if (dev == null) continue;
                foreach (var instance in dev.GetSubKeyNames())
                {
                    using var p = dev.OpenSubKey($@"{instance}\Device Parameters");
                    if (p?.GetValue("PortName") is string name && presentSet.Contains(name) && !found.Contains(name))
                        found.Add(name);
                }
            }
        }
        return found.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose() => _mutex?.Dispose();
}
