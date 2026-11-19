# Changelog

## 1.2.1 — 2026-11-19

### Fixed
- **1:1 plots (e.g. AutoCAD) printed a few percent too small.** The receiver shrank every full-size page to fit inside
  the destination printer's hard margins (about 96% on an A4 laser printer). The new default prints pages at true
  scale (100%) when they fit on the paper, and only shrinks pages larger than the paper. Content in the printer's
  4–5 mm edge margins is cut off instead of the page being scaled.

### Changed
- Receiver Settings: *Page size on the printer* replaces the *Print at actual size* checkbox, with three choices:
  true scale (default), always 100%, or shrink to fit the margins. Receivers upgraded from 1.2.0 or earlier that used
  the old default move to true scale; *actual size* is kept.
- Each printed job's status says the scale used, e.g. "printed at 100% (actual size)" or "shrunk to 71% to fit…".
- CLI: `print --scaling auto|actualsize|fittoprintablearea` (and `--fit`); `--actual-size` still works.
- Licensed under MIT; Setup's license page shows it.

## 1.2.0 — 2026-11-07

### Added
- **Nexora's own printer driver** "Nexora Printer LAN" (v4, text files only: INF, GPD, manifest, empty filter
  pipeline). Papers A0–A5, Letter, Legal, Tabloid, ANSI C/D/E; up to 999 copies; colour/grey/mono; 300/600 dpi.
  Setup signs it with the Nexora driver-signing certificate and trusts that certificate on the PC.
- **No PDF anywhere**: with the Nexora driver, jobs travel as XPS (Windows' own print format, vector).
- Receiver prints XPS **as vectors** through the destination printer's driver (per-page paper, orientation, copies,
  shrink-to-fit), falling back to image printing automatically; setting *Printing jobs from the Nexora driver*.
- CLI: `xpsinfo`, `xpscapture`, `print --method auto|vector|raster`.

### Changed
- Setup's *Print to another PC* role installs the Nexora driver and switches an existing "Nexora Printer LAN" printer
  to it; if the driver cannot be installed it uses Windows' Microsoft Print To PDF driver as before (PDF jobs).
- Uninstall also removes the Nexora driver, its driver-store package and the trusted certificate.
- PDF jobs from 1.0/1.1 clients are still accepted and printed.

## 1.1.0 — 2026-11-06

### Changed
- **One app**: Nexora Client and Nexora Receiver are combined into `NexoraPrinterLAN.exe`. It shows the pages for
  the roles installed on the PC (print to another PC, receive and print, or both), with one tray icon.
- **One Setup**: a Windows Installer package (`NexoraPrinterLAN-<ver>-x64.msi`) replaces the PowerShell
  installers. Custom Setup lets you choose the roles; Modify adds/removes roles; Uninstall removes the printer,
  firewall rules, autostart and shortcuts (user data is kept); upgrades keep roles and settings.
- Installing 1.1 over the 1.0.x apps removes them and keeps pairing, settings, history and the print queue.

### Added
- Receiver setting **Keep copies of printed jobs** (default off): the received PDF — only the transport format —
  is deleted once Windows has sent the job to the printer. Test-mode jobs are always kept.

### Removed
- `installer\Install-Nexora{Client,Receiver}.ps1`, the `.cmd` launchers and per-app zip packages.

## 1.0.1 — 2026-11-05

### Fixed
- Client: saving a pairing code or choosing a receiver now retries waiting jobs immediately instead of after the
  retry backoff (found in the first PC1 → PC2 test, where a held job waited ~18 s).
- Test harness: random free TCP and UDP ports could coincide and fail receiver start-up.

## 1.0.0 — 2026-11-05 (first product build)

### Added
- Windows print queue **Nexora Printer LAN** built from the inbox *Microsoft Print To PDF* driver and a named-pipe
  Local Port; capture of real application jobs (verified: Out-Printer, Notepad, Word, Excel, GDI apps).
- Nexora Client (WPF tray app): dashboard, printers (add/repair/remove/default), devices (discovery, pairing
  status, receiver printers), print jobs (status, retry, cancel, open), settings; durable outbox with resume and
  retry; automatic re-discovery when the receiver's IP changes; restart of stalled spooler jobs.
- Nexora Receiver (WPF tray app): pairing code, test mode, destination printer with real spooler status, printing
  through the destination driver (Windows.Data.Pdf render + GDI), job store with history, reprint/cancel,
  retention.
- NXP/2 protocol: pairing-code mutual authentication, AES-256-GCM, chunked resumable transfer, SHA-256 verified
  from disk, duplicate protection, explicit job states; NexoraDiscovery/2 with HMAC-signed replies.
- `nexora.exe` CLI, PowerShell installers/uninstallers with rollback and scoped firewall rules, diagnostics,
  build/test/package scripts, 63 unit tests and 19 integration tests.

### Changed
- Replaced prototypes (`legacy/`): RAW submission on TCP 9100 and the JSON test receiver are retired.
  Transport moved to TCP 9102; discovery stays on UDP 9101 with a new, incompatible message format.
