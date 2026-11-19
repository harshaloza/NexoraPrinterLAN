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

/// <summary>Where one page goes on the paper: draw it scaled by <see cref="Scale"/> with its top-left corner at (X, Y).</summary>
/// <remarks>All values are in the paper's coordinates (origin at the paper's top-left corner), in the caller's unit.</remarks>
public readonly record struct PagePlacement(double Scale, double X, double Y)
{
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

    /// <summary>Job status text for the smallest scale used on any page.</summary>
    public static string Describe(double minScale) =>
        minScale >= 0.9995 ? "printed at 100% (actual size)" : $"shrunk to {minScale * 100:0.#}% to fit the printer's printable area";
}
