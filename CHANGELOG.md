# Changelog

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
