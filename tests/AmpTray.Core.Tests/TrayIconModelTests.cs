using AmpTray.Ui;
using Xunit;

namespace AmpTray.Tests;

public class TrayIconModelTests
{
    [Fact]
    public void Bars_scale_to_the_wire_limit_in_two_pixel_steps()
    {
        double limit = Levels.WireLimitA(Snap.Make(rating: 600)); // 8.33 A
        Assert.Equal(new[] { 3, 3, 3, 3, 3, 3 }, TrayIconModel.BarHeights(null, limit));
        Assert.Equal(new[] { 3, 3, 3, 3, 3, 3 }, TrayIconModel.BarHeights(Snap.Make(amps: 0), limit));
        // Just under the limit: 9.5 × 0.9996 rounds to 9; at or above it: 9.5 rounds (to even) to 10.
        Assert.Equal(new[] { 21, 21, 21, 21, 21, 21 }, TrayIconModel.BarHeights(Snap.Make(amps: 8.33), limit));
        Assert.Equal(new[] { 23, 23, 23, 23, 23, 23 }, TrayIconModel.BarHeights(Snap.Make(amps: 8.34), limit));
        Assert.Equal(new[] { 23, 23, 23, 23, 23, 23 }, TrayIconModel.BarHeights(Snap.Make(amps: 20), limit)); // clamped
        var half = TrayIconModel.BarHeights(Snap.Make(amps: 4.1667), limit);
        Assert.Equal(3 + 2 * 5, half[0]); // round(9.5 * 0.5) = round(4.75) = 5
        // A ripple smaller than a step gives the same bars, so the icon is not redrawn.
        Assert.Equal(TrayIconModel.BarHeights(Snap.Make(amps: 4.1), limit), TrayIconModel.BarHeights(Snap.Make(amps: 4.2), limit));
        var mixed = TrayIconModel.BarHeights(Snap.Make(amps: 1).WithCurrents(9, 0), limit);
        Assert.Equal(23, mixed[0]);
        Assert.Equal(3, mixed[1]);
        Assert.Equal(5, mixed[2]); // 1 / 8.33 = 0.12 → round(1.14) = 1
    }

    [Fact]
    public void Colour_key_and_dedupe_key()
    {
        Assert.Equal("IdleBrush", TrayIconModel.ColorKey(null, alerting: false));
        Assert.Equal("CritBrush", TrayIconModel.ColorKey(Snap.Make(amps: 1), alerting: true));
        Assert.Equal("OkBrush", TrayIconModel.ColorKey(Snap.Make(amps: 1), alerting: false));
        Assert.Equal("WarnBrush", TrayIconModel.ColorKey(Snap.Make(amps: 8.0), alerting: false));

        string a = TrayIconModel.Key(0xFF3DD6A0, 0xFF14161B, new[] { 3, 5, 7, 9, 11, 13 });
        Assert.Equal("4282242720:4279506459:3,5,7,9,11,13", a);
        Assert.Equal(a, TrayIconModel.Key(0xFF3DD6A0, 0xFF14161B, new[] { 3, 5, 7, 9, 11, 13 }));
        Assert.NotEqual(a, TrayIconModel.Key(0xFF3DD6A0, 0xFF14161B, new[] { 3, 5, 7, 9, 11, 15 }));
        Assert.NotEqual(a, TrayIconModel.Key(0xFFF0524F, 0xFF14161B, new[] { 3, 5, 7, 9, 11, 13 }));
    }

    [Fact]
    public void Geometry_matches_the_32_px_icon()
    {
        Assert.Equal(32, TrayIconModel.Size);
        Assert.Equal(4, TrayIconModel.BarX(0));
        Assert.Equal(24, TrayIconModel.BarX(5));
        // Six bars end at x = 27, inside the 32 px icon; the tallest bar (23 px) starts at y = 4.
        Assert.Equal(27, TrayIconModel.BarX(5) + TrayIconModel.BarWidth);
        Assert.Equal(4, TrayIconModel.Bottom - 23);
    }
}
