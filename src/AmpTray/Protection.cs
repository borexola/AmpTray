using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AmpTray.Device;

namespace AmpTray;

/// <summary>User-defined rules that shut the PC down. Durations are in minutes; anything under 10 s is raised to 10 s.</summary>
public sealed class ShutdownSettings
{
    public bool Enabled { get; set; }
    public int CountdownSeconds { get; set; } // 0 = immediate

    public bool TempRule { get; set; } = true;
    public int TempSensor { get; set; } // 0 = any, 1..4 = IN, OUT, TS1, TS2
    public double TempAboveC { get; set; } = 80;
    public double TempMinutes { get; set; } = 1;

    public bool WireRule { get; set; } = true;
    public int WireCount { get; set; } = 1;
    public double WireAboveA { get; set; } = 9.2;
    public double WireMinutes { get; set; } = 1;

    public bool FaultRule { get; set; }
    public ushort FaultMask { get; set; } = 0x3F; // all six fault types
    public double FaultMinutes { get; set; } = 1;

    public bool PowerRule { get; set; }
    public double PowerAboveW { get; set; } = 600;
    public double PowerMinutes { get; set; } = 1;

    public bool GpuTempRule { get; set; } = true;
    public double GpuTempAboveC { get; set; } = 95;
    public double GpuTempMinutes { get; set; } = 1;

    public static readonly string[] SensorKeys = { "Any sensor", "IN", "OUT", "TS1", "TS2" };
    public static string SensorName(int i) => Loc.T(SensorKeys[Math.Clamp(i, 0, 4)]);
}

public enum ShutdownRule { Temperature, Wires, Fault, Power, GpuTemperature }

public sealed record PendingRule(ShutdownRule Rule, string Condition, TimeSpan Remaining);

public sealed record ShutdownEvaluation(IReadOnlyList<PendingRule> Pending, PendingRule? Triggered);

/// <summary>
/// Tracks how long each rule's condition has held. A rule fires only when ALL of these hold:
///   * its condition has been true for the configured duration, and at least <see cref="MinGrace"/>;
///   * at least <see cref="MinConsecutive"/> fresh readings from its own source confirmed it;
///   * the current reading still satisfies it.
/// Brief dips below the threshold (under <see cref="ClearGrace"/>) do not reset the timer, so a
/// threshold-hugging sensor cannot defeat a rule. A gap in readings longer than <see cref="MaxGap"/>
/// (unplugged device, sleep/resume, clock change) resets it, so time spent asleep never counts.
/// </summary>
public sealed class ShutdownMonitor
{
    public const int MinConsecutive = 2;
    public static readonly TimeSpan MinGrace = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ClearGrace = TimeSpan.FromSeconds(10);

    /// <summary>Longest silence from a rule's data source before its timer is thrown away.</summary>
    public TimeSpan MaxGap { get; set; } = TimeSpan.FromSeconds(10);

    private sealed class State
    {
        public DateTime? Since;
        public DateTime LastTrue;
        public DateTime LastFresh;
        public int Count;
        public bool Suppressed;
        public string Condition = "";
    }

    private readonly Dictionary<ShutdownRule, State> _states = Enum.GetValues<ShutdownRule>().ToDictionary(r => r, _ => new State());

    /// <summary>
    /// Evaluates every rule against the latest readings. <paramref name="deviceFresh"/> / <paramref name="gpuFresh"/>
    /// say which source produced a new reading for this call; only fresh readings count towards confirmation.
    /// A null <paramref name="s"/> or <paramref name="gpu"/> means that source has no data right now.
    /// </summary>
    public ShutdownEvaluation Evaluate(SensorSnapshot? s, Gpu.GpuSnapshot? gpu, DateTime now, ShutdownSettings cfg,
        bool deviceFresh, bool gpuFresh)
    {
        var pending = new List<PendingRule>();
        PendingRule? triggered = null;

        void Check(ShutdownRule rule, bool enabled, string? condition, double minutes, bool fresh)
        {
            var st = _states[rule];
            if (!cfg.Enabled || !enabled)
            {
                Clear(st);
                return;
            }

            // Readings stopped for a while (unplug, sleep, clock step): whatever was timing is void.
            if (st.Since != null && now - st.LastFresh > MaxGap) Clear(st);
            if (fresh) st.LastFresh = now;

            bool held = condition != null;
            if (held)
            {
                st.Since ??= now;
                st.LastTrue = now;
                st.Condition = condition!;
                if (fresh) st.Count++;
            }
            else
            {
                if (st.Since == null) return;
                if (now - st.LastTrue > ClearGrace)
                {
                    Clear(st); // genuinely cleared: a cancelled rule is live again too
                    return;
                }
                // Brief dip: keep timing, keep showing the last condition.
            }

            if (st.Suppressed) return;

            var duration = TimeSpan.FromMinutes(Math.Max(0, minutes));
            if (duration < MinGrace) duration = MinGrace;
            var remaining = duration - (now - st.Since!.Value);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            var p = new PendingRule(rule, st.Condition, remaining);
            pending.Add(p);
            if (held && remaining == TimeSpan.Zero && st.Count >= MinConsecutive) triggered ??= p;
        }

        Check(ShutdownRule.Temperature, cfg.TempRule, s == null ? null : TempCondition(s, cfg), cfg.TempMinutes, deviceFresh);
        Check(ShutdownRule.Wires, cfg.WireRule, s == null ? null : WireCondition(s, cfg), cfg.WireMinutes, deviceFresh);
        Check(ShutdownRule.Fault, cfg.FaultRule, s == null ? null : FaultCondition(s, cfg), cfg.FaultMinutes, deviceFresh);
        Check(ShutdownRule.Power, cfg.PowerRule, s == null ? null : PowerCondition(s, cfg), cfg.PowerMinutes, deviceFresh);
        Check(ShutdownRule.GpuTemperature, cfg.GpuTempRule, gpu == null ? null : GpuTempCondition(gpu, cfg), cfg.GpuTempMinutes, gpuFresh);

        return new ShutdownEvaluation(pending, triggered);
    }

    /// <summary>Stops the given rules from firing until their condition clears.</summary>
    public void Suppress(IEnumerable<ShutdownRule> rules)
    {
        foreach (var r in rules) _states[r].Suppressed = true;
    }

    /// <summary>Forget all timers (disarmed, resumed from sleep, clock changed).</summary>
    public void Reset()
    {
        foreach (var st in _states.Values) Clear(st);
    }

    private static void Clear(State st)
    {
        st.Since = null;
        st.Count = 0;
        st.Suppressed = false;
        st.Condition = "";
    }

    private static string? TempCondition(SensorSnapshot s, ShutdownSettings c)
    {
        double[] t = { s.TempInC, s.TempOutC, s.TempTs1C, s.TempTs2C };
        var hits = Enumerable.Range(0, 4)
            .Where(i => (c.TempSensor == 0 || c.TempSensor == i + 1) && SensorSnapshot.IsTempPresent(t[i]) && t[i] >= c.TempAboveC)
            .Select(i => $"{ShutdownSettings.SensorName(i + 1)} {t[i]:0.0}°C")
            .ToList();
        return hits.Count == 0 ? null : $"{string.Join(", ", hits)} ≥ {c.TempAboveC:0.#}°C";
    }

    private static string? WireCondition(SensorSnapshot s, ShutdownSettings c)
    {
        var hot = Enumerable.Range(0, SensorSnapshot.PinCount).Where(i => s.PinCurrent[i] >= c.WireAboveA).ToList();
        return hot.Count >= Math.Max(1, c.WireCount)
            ? Loc.F("{0} wire(s) ≥ {1} A (#{2})", hot.Count, c.WireAboveA.ToString("0.0#"), string.Join(", #", hot.Select(i => i + 1)))
            : null;
    }

    private static string? FaultCondition(SensorSnapshot s, ShutdownSettings c)
    {
        ushort hit = (ushort)(s.FaultStatus & c.FaultMask);
        return hit == 0 ? null : Loc.T("fault:") + " " + string.Join(", ", SensorSnapshot.Decode(hit).Select(SensorSnapshot.Describe));
    }

    private static string? PowerCondition(SensorSnapshot s, ShutdownSettings c) =>
        s.TotalPowerW >= c.PowerAboveW ? Loc.F("power {0} W ≥ {1} W", s.TotalPowerW.ToString("0"), c.PowerAboveW.ToString("0")) : null;

    private static string? GpuTempCondition(Gpu.GpuSnapshot g, ShutdownSettings c) =>
        g.TempC >= c.GpuTempAboveC ? $"GPU {g.TempC:0}°C ≥ {c.GpuTempAboveC:0.#}°C" : null;

    public static string Describe(ShutdownRule rule, ShutdownSettings c) => rule switch
    {
        ShutdownRule.Temperature => Loc.F("{0} ≥ {1}°C for {2}", ShutdownSettings.SensorName(c.TempSensor), c.TempAboveC.ToString("0.#"), Minutes(c.TempMinutes)),
        ShutdownRule.Wires => Loc.F("{0}+ wire(s) ≥ {1} A for {2}", c.WireCount, c.WireAboveA.ToString("0.0#"), Minutes(c.WireMinutes)),
        ShutdownRule.Fault => Loc.F("device fault for {0}", Minutes(c.FaultMinutes)),
        ShutdownRule.Power => Loc.F("power ≥ {0} W for {1}", c.PowerAboveW.ToString("0"), Minutes(c.PowerMinutes)),
        ShutdownRule.GpuTemperature => Loc.F("GPU ≥ {0}°C for {1}", c.GpuTempAboveC.ToString("0.#"), Minutes(c.GpuTempMinutes)),
        _ => rule.ToString(),
    };

    public static string Minutes(double m)
    {
        double seconds = Math.Max(m * 60, MinGrace.TotalSeconds);
        return seconds < 60 ? Loc.F("{0} s", seconds.ToString("0")) : Loc.F("{0} min", (seconds / 60).ToString("0.##"));
    }

    public static IEnumerable<string> ActiveRuleDescriptions(ShutdownSettings c)
    {
        if (c.TempRule) yield return Describe(ShutdownRule.Temperature, c);
        if (c.WireRule) yield return Describe(ShutdownRule.Wires, c);
        if (c.FaultRule) yield return Describe(ShutdownRule.Fault, c);
        if (c.PowerRule) yield return Describe(ShutdownRule.Power, c);
        if (c.GpuTempRule) yield return Describe(ShutdownRule.GpuTemperature, c);
    }
}

/// <summary>Runs the actual Windows shutdown and keeps an audit trail.</summary>
public static class PcShutdown
{
    public static string LogFile(string logFolder) => Path.Combine(logFolder, "shutdown_events.log");

    public static void Log(string logFolder, string line)
    {
        try
        {
            Directory.CreateDirectory(logFolder);
            File.AppendAllText(LogFile(logFolder), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch
        {
            // Never let logging stop a protective shutdown.
        }
    }

    /// <summary>
    /// Immediate forced shutdown: /f closes apps that would otherwise block it with save prompts.
    /// Returns false if shutdown.exe could not be started; a later refusal by Windows (non-zero exit,
    /// e.g. 1190 = a shutdown is already scheduled) is reported through <paramref name="onFailure"/>.
    /// </summary>
    public static bool Execute(string reason, string logFolder, Action<string> onFailure)
    {
        if (Environment.GetEnvironmentVariable("WIREVIEW_SHUTDOWN_DRYRUN") == "1")
        {
            Log(logFolder, "DRY RUN (WIREVIEW_SHUTDOWN_DRYRUN=1), would shut down now: " + reason);
            return true;
        }
        Log(logFolder, "SHUTDOWN: " + reason);

        string comment = (Loc.T("AmpTray auto shutdown:") + " " + reason).Replace("\"", "'");
        if (comment.Length > 500) comment = comment[..500];
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = $"/s /f /t 0 /c \"{comment}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log(logFolder, "Shutdown FAILED to start: " + ex.Message);
            onFailure(ex.Message);
            return false;
        }
        if (p == null)
        {
            onFailure("shutdown.exe could not be started.");
            return false;
        }

        // Don't block the UI thread on the exit code.
        _ = Task.Run(() =>
        {
            using (p)
            {
                if (p.WaitForExit(5000) && p.ExitCode != 0)
                {
                    string msg = $"shutdown.exe exited with code {p.ExitCode}.";
                    Log(logFolder, "Shutdown FAILED: " + msg);
                    onFailure(msg);
                }
            }
        });
        return true;
    }
}
