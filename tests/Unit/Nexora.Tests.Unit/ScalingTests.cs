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

public class PrintCalibrationTests
{
    [Fact]
    public void Portrait_page_is_stretched_along_the_feed_only()
    {
        var p = new PagePlacement(1, 10, 20).Calibrate(new PrintCalibration(100, 101), landscape: false);
        Assert.Equal(1, p.ScaleX, 9);
        Assert.Equal(1.01, p.ScaleY, 9);
        Assert.Equal(10, p.X, 9);
        Assert.Equal(20.2, p.Y, 9);
        Assert.Equal(1, p.Scale); // reported page scaling is unchanged
    }

    [Fact]
    public void Landscape_page_gets_the_feed_correction_on_its_width()
    {
        var p = new PagePlacement(0.5, 0, 0).Calibrate(new PrintCalibration(99, 101), landscape: true);
        Assert.Equal(0.505, p.ScaleX, 9);
        Assert.Equal(0.495, p.ScaleY, 9);
    }

    [Fact]
    public void No_calibration_leaves_the_placement_alone()
    {
        var p = new PagePlacement(1, 3, 4);
        Assert.Equal(p, p.Calibrate(null, false));
        Assert.Equal(p, p.Calibrate(new PrintCalibration(), true));
    }

    [Fact]
    public void Measurement_gives_the_new_correction_relative_to_the_current_one()
    {
        // 250 mm printed as 248 mm → +0.81%; measured again with that correction as 250.5 mm → slightly less.
        var first = new PrintCalibration().FromMeasurement(150, 150, 250, 248);
        Assert.Equal(100, first.AcrossPercent);
        Assert.Equal(100.81, first.AlongFeedPercent);
        var second = first.FromMeasurement(150, 150, 250, 250.5);
        Assert.Equal(100.61, second.AlongFeedPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(200)] // 25% correction: a mistyped measurement, refused
    public void Implausible_measurements_are_refused(double measuredAlong) =>
        Assert.Throws<InvalidOperationException>(() => new PrintCalibration().FromMeasurement(150, 150, 250, measuredAlong));

    [Fact]
    public void Job_status_mentions_the_calibration()
    {
        Assert.Equal("printed at 100% (actual size)", PagePlacement.Describe(1, new PrintCalibration()));
        Assert.Contains("100.81% along the paper", PagePlacement.Describe(1, new PrintCalibration(100, 100.81)));
    }

    [Fact]
    public void Calibration_is_stored_per_printer_and_survives_a_save()
    {
        var c = new ReceiverConfig();
        c.SetCalibration("Canon LBP2900", new PrintCalibration(100, 100.8));
        var back = JsonSerializer.Deserialize<ReceiverConfig>(JsonSerializer.Serialize(c, Json.Indented), Json.Indented)!;
        back.Validate();
        Assert.Equal(100.8, back.CalibrationFor("canon lbp2900")!.AlongFeedPercent);
        Assert.Null(back.CalibrationFor("Microsoft Print to PDF"));

        back.SetCalibration("CANON LBP2900", new PrintCalibration()); // 100% × 100% removes it
        Assert.Null(back.CalibrationFor("Canon LBP2900"));
        Assert.Empty(back.Calibrations);
    }

    [Fact]
    public void Calibration_page_is_a_valid_one_page_pdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexora-cal-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Nexora.Shared.Pdf.TestPdfBuilder.BuildCalibrationPage("Canon (LBP2900)", "no size calibration"));
        try
        {
            var check = Nexora.Shared.Pdf.DocumentFormats.Detect(path);
            Assert.Null(check.Problem);
            Assert.Equal(1, check.PageCount);
        }
        finally { File.Delete(path); }
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
