using System;
using System.Collections.Generic;
using System.IO;

namespace AmpTray.Ui;

/// <summary>The fault alarm sound as a WAV file in memory. Duplicated from the WPF FaultAlert.</summary>
public static class AlarmWave
{
    public const int SampleRate = 22050;

    /// <summary>The two tones of the alarm and the pauses between them, in play order.</summary>
    public static readonly (double Hz, double Seconds)[] Tones =
        { (960, 0.18), (0, 0.04), (720, 0.18), (0, 0.04), (960, 0.18), (0, 0.04), (720, 0.18), (0, 0.36) };

    /// <summary>Volume percentages outside this range are clamped before building.</summary>
    public const int MinVolumePercent = 5, MaxVolumePercent = 100;

    /// <summary>One loop of a two-tone alarm: 960 Hz / 720 Hz beeps with a short pause, 16-bit mono PCM.</summary>
    public static byte[] Build(double volume)
    {
        var samples = new List<short>();
        foreach (var (hz, seconds) in Tones)
        {
            int n = (int)(SampleRate * seconds);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - i) / (SampleRate * 0.008)); // 8 ms fade to avoid clicks
                double v = hz == 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / SampleRate) * env * volume * 0.9;
                // Add a touch of the 3rd harmonic so it cuts through like a buzzer.
                if (hz > 0) v += Math.Sin(6 * Math.PI * hz * i / SampleRate) * env * volume * 0.1;
                samples.Add((short)(v * short.MaxValue));
            }
        }

        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Count * 2;
            bw.Write("RIFF"u8); bw.Write(36 + dataBytes); bw.Write("WAVE"u8);
            bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
            bw.Write(SampleRate); bw.Write(SampleRate * 2); bw.Write((short)2); bw.Write((short)16);
            bw.Write("data"u8); bw.Write(dataBytes);
            foreach (var s in samples) bw.Write(s);
        }
        return ms.ToArray();
    }

    /// <summary>Builds the alarm for a volume setting in percent (clamped to 5..100).</summary>
    public static byte[] BuildPercent(int volumePercent) =>
        Build(Math.Clamp(volumePercent, MinVolumePercent, MaxVolumePercent) / 100.0);
}
