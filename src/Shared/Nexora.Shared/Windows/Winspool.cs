using System.ComponentModel;
using System.Runtime.InteropServices;
using Nexora.Shared.Jobs;

namespace Nexora.Shared.Windows;

public sealed record SpoolerPrinter(string Name, string? DriverName, string? PortName, string? Comment, uint Attributes, uint Status, uint JobCount)
{
    public const uint AttrDefault = 0x4, AttrNetwork = 0x10, AttrLocal = 0x40, AttrWorkOffline = 0x400;

    public bool WorkOffline => (Attributes & AttrWorkOffline) != 0;
    /// <summary>Ready = Windows does not report the queue as offline, paused, in error or otherwise unusable.</summary>
    public bool IsReady => !WorkOffline && (Status & Winspool.PrinterStatusBlocking) == 0;
    public string StatusText => Winspool.DescribePrinterStatus(Status, Attributes);
}

public sealed record SpoolerJob(uint JobId, string PrinterName, string? Document, string? UserName, string? MachineName,
    uint Status, string? StatusText, uint TotalPages, uint PagesPrinted, uint SizeBytes, PrintSettings? Settings)
{
    public bool IsPrinting => (Status & Winspool.JobPrinting) != 0;
    public bool HasError => (Status & (Winspool.JobError | Winspool.JobOffline | Winspool.JobPaperOut | Winspool.JobUserIntervention | Winspool.JobBlockedDevq)) != 0;
    public bool IsDone => (Status & (Winspool.JobPrinted | Winspool.JobComplete | Winspool.JobDeleted)) != 0;
}

/// <summary>Minimal, documented winspool.drv interop (EnumPrinters/EnumJobs/SetJob/default printer).</summary>
public static class Winspool
{
    // Printer status flags (winspool.h)
    public const uint PsPaused = 0x1, PsError = 0x2, PsPendingDeletion = 0x4, PsPaperJam = 0x8, PsPaperOut = 0x10,
        PsManualFeed = 0x20, PsPaperProblem = 0x40, PsOffline = 0x80, PsBusy = 0x200, PsPrinting = 0x400,
        PsOutputBinFull = 0x800, PsNotAvailable = 0x1000, PsWaiting = 0x2000, PsProcessing = 0x4000, PsInitializing = 0x8000,
        PsWarmingUp = 0x10000, PsTonerLow = 0x20000, PsNoToner = 0x40000, PsUserIntervention = 0x100000,
        PsOutOfMemory = 0x200000, PsDoorOpen = 0x400000, PsServerUnknown = 0x800000, PsPowerSave = 0x1000000;

    public const uint PrinterStatusBlocking = PsPaused | PsError | PsPendingDeletion | PsPaperJam | PsPaperOut | PsPaperProblem |
        PsOffline | PsNotAvailable | PsNoToner | PsUserIntervention | PsOutOfMemory | PsDoorOpen | PsServerUnknown;

    // Job status flags
    public const uint JobPaused = 0x1, JobError = 0x2, JobDeleting = 0x4, JobSpooling = 0x8, JobPrinting = 0x10, JobOffline = 0x20,
        JobPaperOut = 0x40, JobPrinted = 0x80, JobDeleted = 0x100, JobBlockedDevq = 0x200, JobUserIntervention = 0x400,
        JobRestart = 0x800, JobComplete = 0x1000, JobRetained = 0x2000;

    public const uint JobControlPause = 1, JobControlResume = 2, JobControlCancel = 3, JobControlRestart = 4, JobControlDelete = 5;

    private const uint PRINTER_ENUM_LOCAL = 0x2, PRINTER_ENUM_CONNECTIONS = 0x4;

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrintersW(uint flags, string? name, uint level, IntPtr pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumJobsW(IntPtr hPrinter, uint firstJob, uint noJobs, uint level, IntPtr pJob, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetJobW(IntPtr hPrinter, uint jobId, uint level, IntPtr pJob, uint command);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDefaultPrinterW(char[]? pszBuffer, ref int pcchBuffer);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDefaultPrinterW(string pszPrinter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_2
    {
        public IntPtr pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation, pDevMode,
            pSepFile, pPrintProcessor, pDatatype, pParameters, pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Ms; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOB_INFO_2
    {
        public uint JobId;
        public IntPtr pPrinterName, pMachineName, pUserName, pDocument, pNotifyName, pDatatype, pPrintProcessor,
            pParameters, pDriverName, pDevMode, pStatus, pSecurityDescriptor;
        public uint Status, Priority, Position, StartTime, UntilTime, TotalPages, Size;
        public SYSTEMTIME Submitted;
        public uint Time, PagesPrinted;
    }

    public static IReadOnlyList<SpoolerPrinter> EnumeratePrinters()
    {
        var list = new List<SpoolerPrinter>();
        uint flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        EnumPrintersW(flags, null, 2, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0) return list;
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrintersW(flags, null, 2, buf, needed, out _, out uint count)) throw new Win32Exception();
            int size = Marshal.SizeOf<PRINTER_INFO_2>();
            for (int i = 0; i < count; i++)
            {
                var p = Marshal.PtrToStructure<PRINTER_INFO_2>(buf + i * size);
                list.Add(new SpoolerPrinter(Marshal.PtrToStringUni(p.pPrinterName) ?? "", Marshal.PtrToStringUni(p.pDriverName),
                    Marshal.PtrToStringUni(p.pPortName), Marshal.PtrToStringUni(p.pComment), p.Attributes, p.Status, p.cJobs));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrinterDriversW(string? pName, string? pEnvironment, uint level, IntPtr pDriverInfo, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    /// <summary>Names of the printer drivers installed for this machine's architecture (no admin rights needed).</summary>
    public static IReadOnlyList<string> EnumerateDriverNames()
    {
        var names = new List<string>();
        EnumPrinterDriversW(null, null, 1, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0) return names;
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinterDriversW(null, null, 1, buf, needed, out _, out uint count)) throw new Win32Exception();
            for (int i = 0; i < count; i++)
                names.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf + i * IntPtr.Size)) ?? "");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return names;
    }

    public static SpoolerPrinter? FindPrinter(string name) =>
        EnumeratePrinters().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<SpoolerJob> EnumerateJobs(string printerName)
    {
        if (!OpenPrinterW(printerName, out var h, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenPrinter('{printerName}')");
        try
        {
            var list = new List<SpoolerJob>();
            EnumJobsW(h, 0, 512, 2, IntPtr.Zero, 0, out uint needed, out _);
            if (needed == 0) return list;
            var buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!EnumJobsW(h, 0, 512, 2, buf, needed, out _, out uint count)) throw new Win32Exception();
                int size = Marshal.SizeOf<JOB_INFO_2>();
                for (int i = 0; i < count; i++)
                {
                    var j = Marshal.PtrToStructure<JOB_INFO_2>(buf + i * size);
                    list.Add(new SpoolerJob(j.JobId, Marshal.PtrToStringUni(j.pPrinterName) ?? printerName, Marshal.PtrToStringUni(j.pDocument),
                        Marshal.PtrToStringUni(j.pUserName), Marshal.PtrToStringUni(j.pMachineName), j.Status, Marshal.PtrToStringUni(j.pStatus),
                        j.TotalPages, j.PagesPrinted, j.Size, DevMode.Read(j.pDevMode)));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return list;
        }
        finally
        {
            ClosePrinter(h);
        }
    }

    public static bool ControlJob(string printerName, uint jobId, uint command)
    {
        if (!OpenPrinterW(printerName, out var h, IntPtr.Zero)) return false;
        try { return SetJobW(h, jobId, 0, IntPtr.Zero, command); }
        finally { ClosePrinter(h); }
    }

    public static string? GetDefaultPrinter()
    {
        int len = 0;
        GetDefaultPrinterW(null, ref len);
        if (len == 0) return null;
        var buf = new char[len];
        return GetDefaultPrinterW(buf, ref len) ? new string(buf, 0, Math.Max(0, len - 1)) : null;
    }

    public static void SetDefaultPrinter(string name)
    {
        if (!SetDefaultPrinterW(name)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetDefaultPrinter('{name}')");
    }

    public static string DescribePrinterStatus(uint status, uint attributes)
    {
        var parts = new List<string>();
        if ((attributes & SpoolerPrinter.AttrWorkOffline) != 0) parts.Add("Offline (Use Printer Offline / device not connected)");
        (uint flag, string text)[] map =
        [
            (PsPaused, "Paused"), (PsError, "Error"), (PsPendingDeletion, "Being deleted"), (PsPaperJam, "Paper jam"),
            (PsPaperOut, "Out of paper"), (PsManualFeed, "Manual feed"), (PsPaperProblem, "Paper problem"), (PsOffline, "Offline"),
            (PsBusy, "Busy"), (PsPrinting, "Printing"), (PsOutputBinFull, "Output bin full"), (PsNotAvailable, "Not available"),
            (PsWaiting, "Waiting"), (PsProcessing, "Processing"), (PsInitializing, "Initializing"), (PsWarmingUp, "Warming up"),
            (PsTonerLow, "Toner low"), (PsNoToner, "No toner"), (PsUserIntervention, "Needs attention"),
            (PsOutOfMemory, "Out of memory"), (PsDoorOpen, "Door open"), (PsServerUnknown, "Server unknown"), (PsPowerSave, "Power save"),
        ];
        foreach (var (flag, text) in map) if ((status & flag) != 0) parts.Add(text);
        return parts.Count == 0 ? "Ready" : string.Join(", ", parts);
    }
}

/// <summary>Reads the fields Nexora needs from a DEVMODEW (wingdi.h layout).</summary>
public static class DevMode
{
    private const uint DM_ORIENTATION = 0x1, DM_PAPERSIZE = 0x2, DM_PAPERLENGTH = 0x4, DM_PAPERWIDTH = 0x8, DM_COPIES = 0x100,
        DM_PRINTQUALITY = 0x400, DM_COLOR = 0x800, DM_DUPLEX = 0x1000, DM_COLLATE = 0x8000, DM_FORMNAME = 0x10000;

    public static PrintSettings? Read(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        ushort dmSize = (ushort)Marshal.ReadInt16(p, 68);
        if (dmSize < 102) return null;
        uint fields = (uint)Marshal.ReadInt32(p, 72);
        short orientation = Marshal.ReadInt16(p, 76), paper = Marshal.ReadInt16(p, 78), length = Marshal.ReadInt16(p, 80),
            width = Marshal.ReadInt16(p, 82), copies = Marshal.ReadInt16(p, 86), quality = Marshal.ReadInt16(p, 90),
            color = Marshal.ReadInt16(p, 92), duplex = Marshal.ReadInt16(p, 94), collate = Marshal.ReadInt16(p, 100);
        string? form = dmSize >= 166 && (fields & DM_FORMNAME) != 0 ? Marshal.PtrToStringUni(p + 102, 32).TrimEnd('\0') : null;

        return new PrintSettings
        {
            Orientation = (fields & DM_ORIENTATION) == 0 ? PageOrientation.Unspecified : orientation == 2 ? PageOrientation.Landscape : PageOrientation.Portrait,
            PaperSize = !string.IsNullOrWhiteSpace(form) ? form : (fields & DM_PAPERSIZE) != 0 ? PaperName(paper) : null,
            PaperLengthTenthMm = (fields & DM_PAPERLENGTH) != 0 && length > 0 ? length : null,
            PaperWidthTenthMm = (fields & DM_PAPERWIDTH) != 0 && width > 0 ? width : null,
            Copies = (fields & DM_COPIES) != 0 && copies > 0 ? copies : 1,
            Collate = (fields & DM_COLLATE) == 0 || collate == 1,
            ColorMode = (fields & DM_COLOR) == 0 ? ColorMode.Unspecified : color == 2 ? ColorMode.Color : ColorMode.Monochrome,
            ResolutionDpi = (fields & DM_PRINTQUALITY) != 0 && quality > 0 ? quality : null,
            Duplex = (fields & DM_DUPLEX) == 0 ? null : duplex switch { 2 => "LongEdge", 3 => "ShortEdge", _ => "Simplex" },
        };
    }

    public static string PaperName(short code) => code switch
    {
        1 => "Letter", 3 => "Tabloid", 4 => "Ledger", 5 => "Legal", 7 => "Executive", 8 => "A3", 9 => "A4",
        11 => "A5", 12 => "B4 (JIS)", 13 => "B5 (JIS)", 66 => "A2", 256 => "Custom", _ => $"DMPAPER {code}",
    };
}
