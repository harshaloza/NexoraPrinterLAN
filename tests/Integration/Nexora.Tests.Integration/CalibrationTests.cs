using System.Drawing;
using System.Drawing.Imaging;
using Nexora.PrinterIntegration;
using Nexora.Shared.Jobs;
using Nexora.Shared.Pdf;
using Nexora.Shared.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Nexora.Tests.Integration;

/// <summary>Printer size calibration, measured on real driver output (Microsoft Print to PDF).</summary>
public class CalibrationTests(ITestOutputHelper output)
{
    private const string Pdf = "Microsoft Print to PDF";

    [Fact]
    public async Task Calibration_page_prints_at_true_scale_and_calibration_stretches_only_along_the_feed()
    {
        if (Winspool.FindPrinter(Pdf) is null) { output.WriteLine("SKIPPED: Microsoft Print to PDF not present"); return; }
        var dir = Directory.CreateTempSubdirectory("nexora-cal").FullName;
        var src = Path.Combine(dir, "cal.pdf");
        File.WriteAllBytes(src, TestPdfBuilder.BuildCalibrationPage(Pdf, "test"));

        var plain = await PrintAndMeasureAsync(src, Path.Combine(dir, "plain.pdf"), null);
        var cal = await PrintAndMeasureAsync(src, Path.Combine(dir, "cal-out.pdf"), new PrintCalibration(100, 102));
        output.WriteLine($"plain {plain.W:0.0} x {plain.H:0.0} mm, calibrated {cal.W:0.0} x {cal.H:0.0} mm");

        // Frame is 150 x 250 mm; allow ~0.5 mm for line width and rendering.
        Assert.InRange(plain.W, 149.3, 150.7);
        Assert.InRange(plain.H, 249.3, 250.7);
        Assert.InRange(cal.W, 149.3, 150.7);
        Assert.InRange(cal.H / plain.H, 1.017, 1.023);
    }

    private static async Task<(double W, double H)> PrintAndMeasureAsync(string src, string dst, PrintCalibration? calibration)
    {
        var outcome = await new GdiPdfPrinter().PrintAsync(new PrintRequest
        {
            JobId = Guid.NewGuid(), DocumentPath = src, DocumentName = "itest-calibration", PrinterName = Pdf,
            Scaling = PrintScaling.Auto, Calibration = calibration, PrintToFile = dst, TrackTimeout = TimeSpan.FromSeconds(30),
        }, null, default);
        Assert.Equal(JobState.Completed, outcome.State);
        Assert.Contains("printed at 100% (actual size)", outcome.Message);
        if (calibration is not null) Assert.Contains("102% along", outcome.Message);
        for (int i = 0; i < 50 && !File.Exists(dst); i++) await Task.Delay(100);

        const int dpi = 200;
        using var pdf = PdfRenderer.Open(dst);
        using var bmp = pdf.Render(0, dpi);
        var box = DarkBounds(bmp);
        return (box.Width * 25.4 / dpi, box.Height * 25.4 / dpi);
    }

    /// <summary>Bounding box of all dark pixels (the frame encloses everything else on the page).</summary>
    private static Rectangle DarkBounds(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            var row = new byte[data.Stride];
            for (int y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < bmp.Width; x++)
                {
                    int i = x * 4;
                    if (row[i] + row[i + 1] + row[i + 2] < 3 * 160)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            Assert.True(maxX > minX && maxY > minY, "no frame found in the printed page");
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
