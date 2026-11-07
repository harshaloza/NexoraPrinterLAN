# DEVELOPMENT_STATUS — Nexora Printer LAN

_Last updated: 2026-11-07 (version 1.2.0, branch `feature/nexora-v4-driver`). Resume from "Exact next action"._

## Completed

| Phase | State |
|---|---|
| 1 Architecture | Named-pipe Local Port + user-mode .NET app. 1.2: Nexora's own v4 driver (XPS pass-through, no DLL) with the inbox *Microsoft Print To PDF* driver as fallback (`docs/ARCHITECTURE.md` §10). |
| 2 LAN transport | NXP/2 (pairing-code auth, AES-GCM, resumable chunks, SHA-256 from disk, duplicates, states) + NexoraDiscovery/2. Formats: PDF and XPS. |
| 3 Print capture | Notepad, Word, Excel, Out-Printer, GDI apps; 1.2 driver delivers vector XPS. NX/AutoCAD/SolidWorks untested. |
| 4 Receiver printing | PDF → images through the destination driver. XPS → vectors (XpsDocumentWriter; per-page paper/orientation, fit, copies) with automatic image fallback. **Canon LBP2900: one page printed correctly with 1.0.1 (user-confirmed)**; 1.2 vector path on the Canon untested. Printed PDFs deleted unless *Keep copies*. |
| 5 GUI | One tray app `NexoraPrinterLAN.exe` (client and/or receiver pages by role); `nexora.exe` CLI. |
| 6 Installation | One WiX MSI with roles. 1.2: installs/removes the signed Nexora driver + certificate, switches the queue to it, A4 default, PDF-driver fallback. Tested on PC1: upgrade 1.1→1.2, uninstall, fresh install. |
| 7 Verification | `docs/TEST_REPORT.md`: 71/71 unit, 25/25 integration. |

## Current state of the PCs

* **PC1** (`HARSHALOZA`, 192.168.1.14): Nexora Printer LAN **1.2.0** (Client role) from the MSI, queue on the
  **Nexora driver**, A4, paired with `PROGRAMMING`. The prototype test queue/driver were removed.
* **PC2** (`PROGRAMMING`, 192.168.1.3): **1.0.0 Receiver**, **print mode**, Canon LBP2900. A 1.0 receiver rejects XPS
  jobs (`UNSUPPORTED_FORMAT`), so **PC2 must be upgraded to 1.2 before printing from PC1 1.2**.

## Environment facts

* Both PCs on 192.168.1.0/24 (moved from the brief's 192.168.0.x); discovery handles it.
* .NET target net8.0 (support ends 10 Nov 2026) → retarget to .NET 10 when its SDK is installed.
* Packaging (`scripts/Package.ps1`) needs: WiX 4.0.5 tool + extensions in `installer/msi/.wix`; WDK Inf2Cat + signtool;
  the local driver-signing certificate "Nexora Printer LAN Driver Signing (local)" in **this PC's CurrentUser\My**
  (non-exportable; create a new one if this PC is lost — PCs must then trust the new certificate) or `-SignCertThumbprint`.
* Leftover from 1.0.0 testing on PC1: Windows-generated firewall rules **NexoraReceiver** (Allow, Public). Remove as
  Administrator: `Remove-NetFirewallRule -DisplayName 'NexoraReceiver'`.

## Remaining tasks

1. **PC2**: install `release\NexoraPrinterLAN-1.2.0-x64.msi` (*Receive and print* is pre-selected over 1.0.x/1.1).
2. Print from Word (multi-page, 2 copies) and NX 12 (landscape drawing) on PC1; check Canon output (vector quality,
   page order, copies, orientation, scaling). If vector printing fails on CAPT, the receiver falls back to images
   automatically — record which path was used (receiver job details).
3. Interactive Setup wizard check (tests used `/qb`); multi-user; Windows Protected Print Mode.
4. Retarget to .NET 10 LTS; optional purchased code-signing certificate (MSI + driver) for PCs you do not manage.

## Exact next action

Copy `release\NexoraPrinterLAN-1.2.0-x64.msi` to PC2 and run it, then print a test document from Word on PC1.
