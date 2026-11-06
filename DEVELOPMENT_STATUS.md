# DEVELOPMENT_STATUS — Nexora Printer LAN

_Last updated: 2026-11-05. Resume from "Exact next action"._

## Completed

| Phase | State |
|---|---|
| 1 Repository & architecture | Done. Prototypes moved to `legacy/`. Architecture chosen and documented (`docs/ARCHITECTURE.md`): inbox *Microsoft Print To PDF* driver + named-pipe Local Port + user-mode .NET apps. |
| 2 LAN receiver & transport | Done. NXP/2 (pairing-code auth, AES-GCM, resumable chunks, SHA-256 from disk, duplicates, states) + NexoraDiscovery/2. Tested on loopback. |
| 3 Windows print pipeline | Done on PC1. Queue installed (approved). Captures Notepad, Word, Excel, Out-Printer, GDI apps. NX/AutoCAD/SolidWorks untested. |
| 4 Receiver print integration | Implemented (Windows.Data.Pdf → GDI through destination driver, paper/orientation matching, copies, colour, spooler tracking). Verified with *Microsoft Print to PDF*. **Canon LBP2900 untested (disconnected).** |
| 5 GUI & integration | Done. WPF tray apps NexoraClient.exe / NexoraReceiver.exe, `nexora.exe` CLI. |
| 6 Installation & packaging | Done: `scripts/Package.ps1` → `release/*.zip` (self-contained). PowerShell installers/uninstallers with firewall scoping and rollback. **Client installer run successfully on PC1**; receiver installer, rollback and uninstall not yet executed. |
| 7 Verification | `docs/TEST_REPORT.md`. 63/63 unit, 19/19 integration passing. Cross-PC and hardware tests pending. |

## Environment facts discovered

* PC1's actual IP is **192.168.0.43** (not .140): DHCP. The design relies on discovery, not fixed IPs.
* PC1's Wi-Fi profile is **Public**. Fine for the client (no inbound rules). PC2 must be **Private** for receiver rules.
* .NET SDK 9.0.320 / runtimes 8 + 9 installed; target is net8.0 (EOL 10 Nov 2026) → retarget to .NET 10 when its SDK is installed (`NexoraTfm` in `Directory.Build.props`).
* Python, C++/WDK are not used by the product.

## Build results

`scripts\Build.ps1` — 0 errors, 0 warnings (Debug and Release, x64). `scripts\Package.ps1` — both packages created.

## Cross-PC result (2026-11-05)

PC1 (192.168.1.14) → receiver `PROGRAMMING` (192.168.1.3) on Wi-Fi "KHODAL 5G": discovery, pairing, auto-select and
delivery of a held job → VALIDATED, saved in test mode on PC2. Both PCs are now on 192.168.1.0/24, not the 192.168.0.x
addresses in the original brief. 1.0.1 (retry immediately when pairing/receiver settings change) built and installed on PC1 as an upgrade; 1.0.0 kept
as rollback. PC2 is now in **print mode with Canon LBP2900**: an 11:00 upgrade-check page reached COMPLETED in PC2's
spooler and **the user confirmed it printed correctly** — first end-to-end physical print (one portrait text page). PC2 still runs Receiver 1.0.0 (unchanged; the fix is client-only).

## Remaining tasks

1. ~~Install receiver on PC2, pair, first cross-PC delivery~~ — done 2026-11-05.
2. Send real documents PC1 → PC2 (Word, Excel, a multi-page/large PDF) and open them on PC2; update TEST_REPORT.
3. Test printing from Siemens NX 12 (and AutoCAD/SolidWorks where available).
4. ~~Canon LBP2900 first physical print~~ — done 2026-11-05 (one page). Extend: multi-page, landscape, copies, drawings, offline/reconnect.
5. Exercise installer upgrade/rollback/uninstall on both PCs.
6. Retarget to .NET 10 LTS.
7. Optional: IPP endpoint (WPP), receiver service mode, MSI packaging, code signing.

## Exact next action

On PC1, print a multi-page Word document (2 copies) and an NX 12 drawing (landscape) to **Nexora Printer LAN**; check
the Canon output on PC2 for page order, copies, orientation and scaling; record results in TEST_REPORT.
