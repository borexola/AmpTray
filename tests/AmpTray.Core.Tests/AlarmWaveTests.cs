using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using AmpTray.Ui;
using Xunit;

namespace AmpTray.Tests;

public class AlarmWaveTests
{
    [Fact]
    public void Header_is_a_valid_mono_16_bit_riff_wave()
    {
        var wav = AlarmWave.Build(0.5);
        var s = wav.AsSpan();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(s[..4]));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(s[4..]));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(s[8..12]));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(s[12..16]));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(s[16..]));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(s[20..]));      // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(s[22..]));      // mono
        Assert.Equal(22050, BinaryPrimitives.ReadInt32LittleEndian(s[24..]));
        Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(s[28..]));  // byte rate
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(s[32..]));      // block align
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(s[34..]));     // bits
        Assert.Equal("data", Encoding.ASCII.GetString(s[36..40]));
        int dataBytes = BinaryPrimitives.ReadInt32LittleEndian(s[40..]);
        Assert.Equal(wav.Length - 44, dataBytes);

        int expectedSamples = AlarmWave.Tones.Sum(t => (int)(AlarmWave.SampleRate * t.Seconds));
        Assert.Equal(expectedSamples * 2, dataBytes);
        Assert.Equal(1.2, AlarmWave.Tones.Sum(t => t.Seconds), 9); // one loop is 1.2 s
    }

    [Fact]
    public void Volume_scales_the_samples_and_silence_stays_silent()
    {
        short Peak(byte[] wav) => Enumerable.Range(0, (wav.Length - 44) / 2)
            .Select(i => Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + 2 * i)))).Max();
        var loud = AlarmWave.Build(1.0);
        var quiet = AlarmWave.Build(0.25);
        Assert.Equal(loud.Length, quiet.Length);
        // 0.9 × fundamental + 0.1 × third harmonic peaks at about 0.80 of full scale.
        Assert.InRange(Peak(loud), short.MaxValue * 0.75, short.MaxValue * 0.85);
        Assert.InRange(Peak(quiet), Peak(loud) * 0.2, Peak(loud) * 0.3);
        Assert.Equal(0, Peak(AlarmWave.Build(0)));

        // The final pause (0.36 s) is all zeros.
        int pause = (int)(AlarmWave.SampleRate * 0.36);
        Assert.All(Enumerable.Range(0, pause).Select(i => BinaryPrimitives.ReadInt16LittleEndian(loud.AsSpan(loud.Length - 2 * (i + 1)))), v => Assert.Equal(0, v));

        Assert.Equal(AlarmWave.Build(0.05), AlarmWave.BuildPercent(1));    // clamped up to 5 %
        Assert.Equal(AlarmWave.Build(1.0), AlarmWave.BuildPercent(150));   // clamped down to 100 %
    }
}
