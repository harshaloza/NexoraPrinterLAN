namespace Nexora.PrinterIntegration;

/// <summary>How a received page is scaled onto the destination printer's paper.</summary>
/// <remarks>Stored by name in the receiver config ("fitToPrintableArea", "actualSize", "auto"); keep the names.</remarks>
public enum PrintScaling
{
    /// <summary>Shrink every page that does not fit inside the printer's printable area (hard margins) — never enlarge.</summary>
    FitToPrintableArea,
    /// <summary>Always 100%, placed at the paper's corner; anything outside the paper or in the hard margins is cut off.</summary>
    ActualSize,
    /// <summary>
    /// 100% (true scale, e.g. CAD drawings at 1:1) when the page fits on the paper, centred on it; content in the printer's
    /// hard margins is cut off. Pages larger than the paper are shrunk into the printable area.
    /// </summary>
    Auto,
}

/// <summary>
/// Per-printer size correction for printers whose output is slightly too long or short in one direction — typically along
/// the paper feed, where a laser printer's rollers decide the length. Percentages refer to the sheet held in portrait:
/// <see cref="AcrossPercent"/> across the paper (short edge), <see cref="AlongFeedPercent"/> along the feed (long edge).
/// 100.5 draws 0.5% larger to make up for a printer that prints 0.5% short.
/// </summary>
public sealed record PrintCalibration(double AcrossPercent = 100, double AlongFeedPercent = 100)
{
    public const double MinPercent = 90, MaxPercent = 110;

    public bool IsNone => Math.Abs(AcrossPercent - 100) < 1e-9 && Math.Abs(AlongFeedPercent - 100) < 1e-9;

    public void Validate()
    {
        if (AcrossPercent is < MinPercent or > MaxPercent || AlongFeedPercent is < MinPercent or > MaxPercent || double.IsNaN(AcrossPercent) || double.IsNaN(AlongFeedPercent))
            throw new InvalidOperationException($"Size calibration must be between {MinPercent:0}% and {MaxPercent:0}%");
    }

    /// <summary>
    /// The correction after printing a frame of the given nominal size with this calibration and measuring it:
    /// new = current × nominal ÷ measured, per direction.
    /// </summary>
    public PrintCalibration FromMeasurement(double nominalAcrossMm, double measuredAcrossMm, double nominalAlongMm, double measuredAlongMm)
    {
        if (measuredAcrossMm <= 0 || measuredAlongMm <= 0) throw new InvalidOperationException("Measured lengths must be greater than 0 mm");
        var next = new PrintCalibration(
            Math.Round(AcrossPercent * nominalAcrossMm / measuredAcrossMm, 2),
            Math.Round(AlongFeedPercent * nominalAlongMm / measuredAlongMm, 2));
        next.Validate();
        return next;
    }

    public override string ToString() => $"size calibration {AcrossPercent:0.##}% across × {AlongFeedPercent:0.##}% along the paper";
}

/// <summary>Where one page goes on the paper: draw it scaled by <see cref="ScaleX"/>/<see cref="ScaleY"/> with its top-left corner at (X, Y).</summary>
/// <remarks>
/// All values are in the paper's coordinates (origin at the paper's top-left corner), in the caller's unit.
/// <see cref="Scale"/> is the page scaling (same in both directions); ScaleX/ScaleY add any printer calibration.
/// </remarks>
public readonly record struct PagePlacement(double Scale, double X, double Y)
{
    public double ScaleX { get; init; } = Scale;
    public double ScaleY { get; init; } = Scale;

    /// <summary>
    /// Applies a printer calibration, stretching around the paper's corner. On a landscape page the paper feed runs along
    /// the page's width, so the two corrections swap.
    /// </summary>
    public PagePlacement Calibrate(PrintCalibration? c, bool landscape)
    {
        if (c is null || c.IsNone) return this;
        double across = c.AcrossPercent / 100, along = c.AlongFeedPercent / 100;
        double cx = landscape ? along : across, cy = landscape ? across : along;
        return this with { X = X * cx, Y = Y * cy, ScaleX = Scale * cx, ScaleY = Scale * cy };
    }

    /// <summary>Pages within this fraction of the paper size count as fitting (rounding between units and drivers).</summary>
    private const double Tolerance = 0.005;

    public static PagePlacement Compute(PrintScaling mode, double pageW, double pageH, double paperW, double paperH,
        double printableX, double printableY, double printableW, double printableH)
    {
        if (pageW <= 0 || pageH <= 0) return new(1, 0, 0);
        if (mode == PrintScaling.ActualSize) return new(1, 0, 0);
        if (mode == PrintScaling.Auto && pageW <= paperW * (1 + Tolerance) && pageH <= paperH * (1 + Tolerance))
            return new(1, (paperW - pageW) / 2, (paperH - pageH) / 2);

        if (printableW <= 0 || printableH <= 0) (printableX, printableY, printableW, printableH) = (0, 0, paperW, paperH);
        double k = Math.Min(1, Math.Min(printableW / pageW, printableH / pageH));
        double w = pageW * k, h = pageH * k;
        return new(k, printableX + (printableW - w) / 2, printableY + (printableH - h) / 2);
    }

    /// <summary>Job status text for the smallest scale used on any page, plus the calibration if one is set.</summary>
    public static string Describe(double minScale, PrintCalibration? calibration = null) =>
        (minScale >= 0.9995 ? "printed at 100% (actual size)" : $"shrunk to {minScale * 100:0.#}% to fit the printer's printable area")
        + (calibration is null || calibration.IsNone ? "" : $", {calibration}");
}
