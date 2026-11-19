using System.Text.Json;
using Nexora.PrinterIntegration;
using Nexora.Receiver.Core;
using Nexora.Shared;
using Xunit;

namespace Nexora.Tests.Unit;

public class PagePlacementTests
{
    // Millimetres. A4 on a laser printer with 4.2 mm hard margins on every side.
    private const double W = 210, H = 297, M = 4.2;

    private static PagePlacement Place(PrintScaling mode, double pageW, double pageH) =>
        PagePlacement.Compute(mode, pageW, pageH, W, H, M, M, W - 2 * M, H - 2 * M);

    [Fact]
    public void Auto_prints_a_full_size_page_at_true_scale()
    {
        // A 1:1 CAD plot on A4 must not shrink just because the printer has margins.
        Assert.Equal(new PagePlacement(1, 0, 0), Place(PrintScaling.Auto, W, H));
    }

    [Fact]
    public void Auto_centres_a_smaller_page_at_true_scale()
    {
        var p = Place(PrintScaling.Auto, 148, 210); // A5 on A4
        Assert.Equal(1, p.Scale);
        Assert.Equal((W - 148) / 2, p.X, 6);
        Assert.Equal((H - 210) / 2, p.Y, 6);
    }

    [Fact]
    public void Auto_tolerates_rounding_but_shrinks_larger_pages_into_the_printable_area()
    {
        Assert.Equal(1, Place(PrintScaling.Auto, W + 0.5, H + 0.5).Scale);

        var a3 = Place(PrintScaling.Auto, 297, 420); // A3 on an A4-only printer
        double k = Math.Min((W - 2 * M) / 297, (H - 2 * M) / 420);
        Assert.Equal(k, a3.Scale, 9);
        Assert.Equal(M + (W - 2 * M - 297 * k) / 2, a3.X, 6);
        Assert.Equal(M + (H - 2 * M - 420 * k) / 2, a3.Y, 6);
    }

    [Fact]
    public void Fit_shrinks_a_full_size_page_inside_the_margins()
    {
        var p = Place(PrintScaling.FitToPrintableArea, W, H);
        Assert.Equal((W - 2 * M) / W, p.Scale, 9); // ≈ 96%: the 1.2.0 behaviour that broke 1:1 plots
        Assert.True(p.X >= M && p.Y >= M);
    }

    [Fact]
    public void Actual_size_never_scales_or_moves_the_page()
    {
        Assert.Equal(new PagePlacement(1, 0, 0), Place(PrintScaling.ActualSize, 297, 420));
    }

    [Fact]
    public void Describe_reports_the_scale()
    {
        Assert.Equal("printed at 100% (actual size)", PagePlacement.Describe(1));
        Assert.Contains("96%", PagePlacement.Describe(0.96));
    }
}

public class ReceiverConfigUpgradeTests
{
    private static ReceiverConfig FromJson(string json) => JsonSerializer.Deserialize<ReceiverConfig>(json, Json.Indented)!;

    [Fact]
    public void Old_default_fit_setting_moves_to_true_scale()
    {
        var c = FromJson("""{ "scaling": "fitToPrintableArea" }""");
        Assert.True(c.Upgrade());
        Assert.Equal(PrintScaling.Auto, c.Scaling);
        Assert.Equal(ReceiverConfig.CurrentSettingsVersion, c.SettingsVersion);
    }

    [Fact]
    public void Old_actual_size_setting_is_kept()
    {
        var c = FromJson("""{ "scaling": "actualSize" }""");
        c.Upgrade();
        Assert.Equal(PrintScaling.ActualSize, c.Scaling);
    }

    [Fact]
    public void Fit_chosen_after_the_upgrade_is_kept()
    {
        var c = FromJson("""{ "settingsVersion": 2, "scaling": "fitToPrintableArea" }""");
        Assert.False(c.Upgrade());
        Assert.Equal(PrintScaling.FitToPrintableArea, c.Scaling);
    }

    [Fact]
    public void New_config_defaults_to_true_scale_and_round_trips()
    {
        var c = new ReceiverConfig();
        c.Upgrade();
        var back = FromJson(JsonSerializer.Serialize(c, Json.Indented));
        Assert.Equal(PrintScaling.Auto, back.Scaling);
        Assert.False(back.Upgrade());
    }
}
