using System.Globalization;
using System.Text;

namespace Nexora.Shared.Pdf;

/// <summary>
/// Generates small, valid PDF documents for transport and printer tests (no third-party libraries).
/// Optional padding adds an incompressible-looking comment stream to produce large test jobs.
/// </summary>
public static class TestPdfBuilder
{
    public static readonly (double W, double H) A4 = (595.28, 841.89);
    public static readonly (double W, double H) A3 = (841.89, 1190.55);
    public static readonly (double W, double H) Letter = (612, 792);

    public static byte[] Build(string title, int pages = 1, (double W, double H)? pageSize = null, long padToBytes = 0, bool landscape = false)
    {
        var (w, h) = pageSize ?? A4;
        if (landscape) (w, h) = (h, w);
        var objects = new List<byte[]>();
        int fontObj = 3, firstPageObj = 4;
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{firstPageObj + i * 2} 0 R"));

        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pages} >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        for (int i = 0; i < pages; i++)
        {
            int contentObj = firstPageObj + i * 2 + 1;
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(w)} {F(h)}] /Resources << /Font << /F1 {fontObj} 0 R >> >> /Contents {contentObj} 0 R >>"));
            var content = new StringBuilder();
            content.Append($"0.2 w 36 36 {F(w - 72)} {F(h - 72)} re S\n");
            content.Append($"BT /F1 22 Tf 72 {F(h - 110)} Td ({Escape(title)}) Tj ET\n");
            content.Append($"BT /F1 12 Tf 72 {F(h - 140)} Td (Nexora Printer LAN test page {i + 1} of {pages}) Tj ET\n");
            content.Append($"BT /F1 10 Tf 72 {F(h - 160)} Td (Page size {F(w)} x {F(h)} pt. Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC) Tj ET\n");
            content.Append("BT /F1 10 Tf 72 72 Td (If this page opens or prints intact, the transfer passed.) Tj ET\n");
            objects.Add(Stream(Encoding.ASCII.GetBytes(content.ToString())));
        }

        var pdf = Assemble(objects, extraBeforeXref: null);
        if (padToBytes > pdf.Length)
        {
            // Pad with an unreferenced stream object (ignored by viewers) of pseudo-random hex data.
            long need = padToBytes - pdf.Length - 64;
            var rnd = new Random(1234);
            var pad = new byte[Math.Max(0, need)];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < pad.Length; i++) pad[i] = (byte)(i % 81 == 80 ? '\n' : hex[rnd.Next(16)]);
            objects.Add(Stream(pad));
            pdf = Assemble(objects, extraBeforeXref: null);
        }
        return pdf;
    }

    /// <summary>Frame measured for printer size calibration: across × along the paper feed, in mm, on portrait A4.</summary>
    public const double CalibrationAcrossMm = 150, CalibrationAlongMm = 250;

    /// <summary>
    /// One portrait A4 page with a centred 150 × 250 mm frame (thin lines, 10 mm ticks on two edges) to measure for
    /// printer size calibration. It must be printed at 100% (true scale).
    /// </summary>
    public static byte[] BuildCalibrationPage(string printer, string currentCalibration)
    {
        var (w, h) = A4;
        const double mm = 72 / 25.4;
        double fw = CalibrationAcrossMm * mm, fh = CalibrationAlongMm * mm;
        double x0 = (w - fw) / 2, y0 = (h - fh) / 2;
        var c = new StringBuilder();
        c.Append($"0.25 w {F(x0)} {F(y0)} {F(fw)} {F(fh)} re S\n");
        for (int i = 1; i < CalibrationAcrossMm / 10; i++) c.Append($"{F(x0 + i * 10 * mm)} {F(y0 + fh)} m {F(x0 + i * 10 * mm)} {F(y0 + fh - (i % 5 == 0 ? 6 : 3) * mm)} l S\n");
        for (int i = 1; i < CalibrationAlongMm / 10; i++) c.Append($"{F(x0)} {F(y0 + i * 10 * mm)} m {F(x0 + (i % 5 == 0 ? 6 : 3) * mm)} {F(y0 + i * 10 * mm)} l S\n");
        double tx = x0 + 14 * mm, ty = y0 + fh - 18 * mm;
        void Line(int size, string text) { c.Append($"BT /F1 {size} Tf {F(tx)} {F(ty)} Td ({Escape(text)}) Tj ET\n"); ty -= size + 6; }
        Line(16, "Nexora size calibration page");
        Line(10, $"Printer: {printer}");
        Line(10, $"Printed with: {currentCalibration}");
        ty -= 8;
        Line(10, $"Measure the outer frame with a ruler:");
        Line(10, $"  X = width across the paper, should be {CalibrationAcrossMm:0} mm");
        Line(10, $"  Y = height along the paper feed, should be {CalibrationAlongMm:0} mm");
        Line(10, "Enter both measurements in Nexora: Settings > Size calibration.");
        Line(10, "Ticks every 10 mm; long ticks every 50 mm.");

        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [4 0 R] /Count 1 >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(w)} {F(h)}] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>"),
            Stream(Encoding.ASCII.GetBytes(c.ToString())),
        };
        return Assemble(objects, extraBeforeXref: null);
    }

    private static byte[] Assemble(List<byte[]> objects, byte[]? extraBeforeXref)
    {
        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b); }
        W("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            W("\nendobj\n");
        }
        if (extraBeforeXref is not null) ms.Write(extraBeforeXref);
        long xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) W($"{off:0000000000} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Stream(byte[] data)
    {
        var head = Encoding.ASCII.GetBytes($"<< /Length {data.Length} >>\nstream\n");
        var tail = Encoding.ASCII.GetBytes("\nendstream");
        return [.. head, .. data, .. tail];
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
