# DEVELOPMENT_STATUS — Nexora Printer LAN

_Last updated: 2026-11-06 (version 1.1.0, branch `feature/v1.1-combined-app`). Resume from "Exact next action"._

## Completed

| Phase | State |
|---|---|
| 1 Repository & architecture | Done. Inbox *Microsoft Print To PDF* driver + named-pipe Local Port + user-mode .NET app (`docs/ARCHITECTURE.md`; §10 explains why there is no custom driver). |
| 2 LAN receiver & transport | Done. NXP/2 (pairing-code auth, AES-GCM, resumable chunks, SHA-256 from disk, duplicates, states) + NexoraDiscovery/2. |
| 3 Windows print pipeline | Done. Captures Notepad, Word, Excel, Out-Printer, GDI apps. NX/AutoCAD/SolidWorks untested. |
| 4 Receiver print integration | Done. Windows.Data.Pdf → GDI through the destination driver. **Canon LBP2900: one page printed correctly (2026-11-05, user-confirmed).** 1.1: received PDF deleted after printing unless *Keep copies of printed jobs*. |
| 5 GUI | 1.1: one tray app `NexoraPrinterLAN.exe` showing client and/or receiver pages by installed role; `nexora.exe` CLI. |
| 6 Installation | 1.1: one WiX MSI `release/NexoraPrinterLAN-1.1.0-x64.msi` with roles, Modify, Uninstall, upgrade from 1.0.x. Tested on PC1: upgrade from 1.0.1, add/remove Receiver role, uninstall, reinstall, same-version upgrade. |
| 7 Verification | `docs/TEST_REPORT.md`: 63/63 unit, 21/21 integration; cross-PC + Canon (1.0.1); Setup lifecycle (1.1.0, PC1). |

## Current state of the PCs

* **PC1** (`HARSHALOZA`, 192.168.1.14, Wi-Fi "KHODAL 5G", Public profile): Nexora Printer LAN **1.1.0** installed
  from the MSI (Client role), running, paired with `PROGRAMMING`. The installed build predates one last change that
  only affects `--snapshot` mode; the MSI in `release/` is the final build.
* **PC2** (`PROGRAMMING`, 192.168.1.3): still the **1.0.0 Receiver**, in **print mode** with the Canon LBP2900.
  Any job sent from PC1 prints on paper. PC2 did not answer discovery on 2026-11-06.

## Environment facts

* Both PCs moved from 192.168.0.x (original brief) to 192.168.1.0/24; discovery handles it.
* .NET target net8.0 (support ends 10 Nov 2026) → retarget to .NET 10 when its SDK is installed (`NexoraTfm` in `Directory.Build.props`).
* Packaging needs the WiX 4.0.5 dotnet tool and its UI/Util/Firewall extensions in `installer/msi/.wix` (git-ignored; see INSTALLATION §10).
* Leftover from 1.0.0 testing on PC1: Windows-generated firewall rules **NexoraReceiver** (Allow, Public, for a deleted
  release-folder exe). Remove as Administrator: `Remove-NetFirewallRule -DisplayName 'NexoraReceiver'`.

## 1.2 in progress — own driver, no PDF (branch `feature/nexora-v4-driver`, not committed yet)

* `driver/NexoraV4`: v4 "Nexora Printer LAN" driver = INF + GPD (A0–A5, Letter, Legal, Tabloid, ANSI C–E, 999 copies,
  colour) + manifest (XpsFormat=XPS) + empty filter pipeline. `driver/Build-DriverPackage.ps1` builds and signs it
  (local self-signed cert "Nexora Printer LAN Driver Signing (local)" in CurrentUser\My, or `-CertThumbprint`).
* Installed on PC1 for testing (approved): cert trusted, driver oem155.inf, queue "Nexora Printer LAN (XPS test)" on
  `\\.\pipe\NexoraPrinterLAN-XPS`. Undo: `driver\Remove-TestDriver.ps1` (Administrator).
* Code: XPS format end to end (client capture, transport, receiver store), `XpsPackageNormalizer` (spool XPS is
  interleaved), `XpsVectorPrinter` (per-page paper/orientation, fit, copies) with automatic raster fallback,
  receiver setting `XpsPrintMethod`, CLI `xpsinfo` / `xpscapture` / `print --method`.
* Next for 1.2: Setup installs driver + certificate and points the main queue at the Nexora driver (A4 default),
  fallback to the inbox driver if the driver cannot be installed; receiver UI for the XPS method; test on PC2/Canon.

## Remaining tasks

1. PC2: install `NexoraPrinterLAN-1.1.0-x64.msi` (it pre-selects *Receive and print* and removes the 1.0.0 Receiver,
   keeping pairing and settings). Check the app shows the receiver pages and the Canon as destination.
2. Send a multi-page Word document (2 copies) and an NX 12 drawing (landscape) PC1 → PC2; check the Canon output
   (page order, copies, orientation, scaling) and that the received PDF is removed after printing.
3. Exercise the interactive Setup wizard once (Custom Setup page) — automated tests used `/qb`.
4. Retarget to .NET 10 LTS; code-sign the MSI (optional, removes SmartScreen warning).
5. Optional: vector print path on the receiver; IPP endpoint for Windows Protected Print Mode; receiver service mode.

## Exact next action

Copy `release\NexoraPrinterLAN-1.1.0-x64.msi` to PC2 and run it (keep *Receive and print on this PC* selected).
