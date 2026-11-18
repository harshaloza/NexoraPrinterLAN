# Nexora Printer LAN — Test report

## 1.2 development: own v4 driver + XPS without PDF (2026-11-06, PC1, branch `feature/nexora-v4-driver`)

Automated: **71/71 unit, 25/25 integration passed** (Debug). New: format detection, XPS piece re-assembly, XPS transfer,
PDF-announced-as-XPS rejection, XPS raster printing through a real driver, XPS vector printing with paper/orientation/copies.

| Test | Result |
|---|---|
| Driver package (INF + GPD + manifest + empty filter pipeline, no DLL) passes Inf2Cat (0 errors, 0 warnings) | Passed |
| Catalog signed with a local self-signed certificate; certificate trusted in LocalMachine Root + TrustedPublisher; `pnputil /add-driver` + `Add-PrinterDriver` on Windows 11 build 26300 | **Passed** — no test-signing, no signature-enforcement change |
| Print dialogs show papers A0–A5, Letter, Legal, Tabloid, ANSI C/D/E; copies up to 999; colour; 300/600 dpi | Passed |
| Out-Printer, Notepad, GDI A3-landscape graphics, Word → test queue → **XPS** on the pipe (no PDF) | Passed |
| Word/Notepad pages arrive as vector text (Glyphs + embedded fonts) and vector shapes, no images | Passed |
| Receiver: XPS printed as images through *Microsoft Print to PDF* (file), A3 landscape preserved | Passed |
| Receiver: XPS printed as vectors to the Nexora test queue: tickets carry copies = 2, ISOA3, Landscape on job and pages | Passed |
| End to end: Word → Nexora driver → client (XPS) → receiver → stored `.xps`, renders correctly | Passed |
| XPS vector printing into a **GDI (v3) driver such as Canon CAPT** (Windows XPS→GDI conversion) | **Not tested** — needs PC2; raster fallback is automatic if it fails |
| Queue default paper | Came out Letter despite GPD default A4 → Setup must set A4 (as for the 1.1 queue) |

### Setup 1.2.0 on PC1 (2026-11-07)

| Test | Result |
|---|---|
| Upgrade 1.1.0 → 1.2.0 (`/qb`): certificate trusted, driver added to the driver store, existing "Nexora Printer LAN" switched from Microsoft Print To PDF to the **Nexora driver**, default paper **A4**, app 1.2.0 running and owning the pipe | Passed |
| Uninstall: queue, port, Nexora driver, driver-store package and certificate removed; user data kept | Passed |
| Fresh install after uninstall: everything back with the Nexora driver | Passed |
| Automated tests on the installed build: 71/71 unit, 25/25 integration (vector-queue test self-skipped because the prototype test queue was removed; it passed earlier) | Passed |
| A real document printed through the installed 1.2 driver to PC2 / Canon | **Not tested** — PC2 is in print mode (real paper); left for the user |
| Receiver on PC2 upgraded to 1.2; vector printing into the Canon CAPT (GDI) driver | **Not tested** |

## Version 1.1.0 (2026-11-06, PC1 192.168.1.14)

Automated: `scripts\Test.ps1` → **63/63 unit, 21/21 integration passed** (Release). The real-queue capture test
self-skipped while the installed app held the pipe; with the app stopped it was run on its own and **passed**.

| Test | Result |
|---|---|
| Combined app `NexoraPrinterLAN.exe` renders all pages for both roles (snapshots in `docs/images/app-*.png`) | Passed |
| Single-file self-contained app and CLI run, including the Windows PDF engine | Passed |
| **Keep copies of printed jobs** — off: PDF deleted after COMPLETED, buttons disabled; on: PDF kept (integration test printing through *Microsoft Print to PDF* into a file) | Passed |
| MSI install over the 1.0.1 PowerShell install (`/qb`): old NexoraClient stopped and removed (folder, autostart, Apps & features entry), Client role pre-selected, queue kept, pairing/receiver/settings kept, new app captures on the pipe | Passed |
| Modify → add *Receive and print*: rules "Nexora Printer LAN - print jobs (TCP-In)" TCP 9102 and "… discovery (UDP-In)" UDP 9101, Allow, **Private**, **LocalSubnet**, program = `NexoraPrinterLAN.exe` (verified with `netsh`) | Passed |
| Modify → remove *Receive and print*: firewall rules removed, Client role and queue untouched | Passed |
| Uninstall: files, queue, port, autostart, Start menu, Apps & features entry removed; `%LOCALAPPDATA%\Nexora` kept | Passed |
| Reinstall after uninstall: queue recreated, app starts and captures | Passed |
| MSI upgrade (final 1.1.0 build over the earlier 1.1.0 test build): previous product removed, one entry left in Apps & features, Client role and queue kept, app starts and captures | Passed |
| Not tested in 1.1.0 | PC2 install/upgrade from 1.0.x Receiver (PC2 was not reachable on 2026-11-06); interactive Setup UI clicks (tests used `/qb`); a real print through the combined app to PC2 (PC2 is in print mode: real paper). |

Found during testing (not caused by Setup): two Windows-generated firewall rules named **NexoraReceiver** allow
`D:\NexoraPrinterLAN\release\nexoraprinterlan-receiver-1.0.0-win-x64\app\nexorareceiver.exe` inbound on the
**Public** profile. They come from a firewall prompt answered during the 1.0.0 packaging smoke test. Removal command
in DEVELOPMENT_STATUS.md.

## Version 1.0.x

**Build:** 1.0.0 · **Date:** 2026-11-05 · **Tested on:** PC1 only (Windows 11 Pro build 26300, x64, Wi-Fi 192.168.0.43, network profile *Public*)
**Cross-PC (2026-11-05, Wi-Fi "KHODAL 5G", 192.168.1.0/24):** PC1 = 192.168.1.14 (client installed), PC2 = receiver
**`PROGRAMMING`** at 192.168.1.3 (installed by the user). Both PCs had moved off the 192.168.0.x addresses in the
original brief; discovery found the receiver without configuration. Earlier the same day PC2 was unreachable at
192.168.0.31, so most automated network tests use loopback (127.0.0.1) on PC1.

Automated results: `scripts\Test.ps1` → **63/63 unit tests passed, 20/20 integration tests passed** (TRX files in `TestResults\`).
In the latest run the queue-capture test self-skipped because the installed Nexora Client owned the pipe; it passed
in earlier runs and the real cross-PC test below exercised the same path.

## Passed

| # | Test | How it was verified |
|---|---|---|
| 1 | Receiver discovery | Integration: UDP discovery finds the receiver, reply HMAC verifies ("paired"); an unpaired client sees it as untrusted; auto-select picks the single paired receiver. Unit: tampered/stale/garbage replies rejected. *Loopback only.* |
| 2 | Connection + mutual authentication | Integration + unit: correct code connects; wrong code → `AUTH_FAILED` from both sides. |
| 3 | Small PDF transfer | Integration + CLI: 2 KB/3-page PDF → `VALIDATED`, stored file SHA-256 identical. |
| 4 | Large PDF transfer | Integration: 60 MB PDF in 256 KiB encrypted chunks; CLI: 42 MB, hash identical. |
| 5 | Checksum verification | Integration: wrong client hash → `FAILED/CHECKSUM_MISMATCH`, nothing stored; client auto-resends. |
| 6 | Interrupted connection | Integration + CLI: connection dropped at 3 MB / 10 MB → reconnect resumes from the received offset (10,223,616 B in the CLI run); final hash identical. |
| 7 | Retry | Integration: receiver absent → job stays `QUEUED` with `UNREACHABLE` and backoff; delivered once the receiver starts. |
| 8 | Duplicate-job prevention | Integration + CLI: same job id offered again → `duplicate`, no second file. |
| 9 | Receiver restart | Integration: records and a partial upload survive a restart; status query and resume work afterwards. |
| 10 | Client restart | Integration: queued job persisted in the outbox, sent by a new client instance (attempts ≥ 2). |
| 11 | Invalid file rejection | Integration + CLI: non-PDF payload → `FAILED/INVALID_DOCUMENT`, quarantined, path-traversal name neutralised; oversized → `TOO_LARGE`; wrong format → `UNSUPPORTED_FORMAT`; spoofed client id → `BAD_REQUEST`. |
| 12 | Out-of-subnet rejection | Integration: client from a non-allowed address gets `NOT_ALLOWED`; discovery stays silent for it. Unit: other private subnet, public IP, explicit allow-list, public addresses refused even if listed. *A real host on another subnet was not used.* |
| 13 | Windows queue creation | `Install-NexoraQueue.ps1` run elevated on PC1 (user approved UAC): queue **Nexora Printer LAN**, driver *Microsoft Print To PDF*, port `\\.\pipe\NexoraPrinterLAN`, status Normal. Visible to `Get-Printer` and print dialogs. |
| 14 | Windows application printing (PC1 → loopback receiver) | Captured and delivered as valid PDFs: PowerShell `Out-Printer`; **Notepad** (`/pt`); **Microsoft Word** (COM, A4 doc with vector shape, 2 copies → 2 pages); **Microsoft Excel** (COM, landscape sheet); a GDI application printing an A3-landscape 2-page PDF (page size preserved 420×297 mm, verified by the automated test `Printing_to_the_windows_queue_is_captured_and_delivered`). Rendered output inspected visually. |
| — | Client not running when printing | Manual: job printed while the client was stopped sat in the Windows queue as *Error*; on client start it was restarted, captured and delivered. |
| — | Receiver print path through a real driver | Integration + CLI: PDF rendered with Windows.Data.Pdf and printed through the *Microsoft Print to PDF* driver to a file; 3 pages, A4/A3 paper and landscape selected correctly; output re-opened and inspected. |
| — | Honest printer failure | Integration: print mode with a non-existent printer → `FAILED/PRINTER_UNAVAILABLE`, document still saved. |
| — | Protocol security | Unit: AES-GCM payload contains no plaintext; tampered and replayed frames detected; oversize frames rejected before allocation; DPAPI secret round-trip. |
| — | GUIs | Both WPF apps start, render every page with live data (snapshots in `docs/images/`), single-instance and tray. |
| A | Client installation on PC1 | `Install-NexoraClient.ps1` from the release package run elevated (approved): files in `C:\Program Files\Nexora Printer LAN\Client`, HKLM autostart, Start menu shortcut, Apps & features entry, queue re-validated (A4 default). App started un-elevated as the user, capture pipe listening. A print via `Out-Printer` was captured by the installed client and held as `QUEUED/AUTH_FAILED — No pairing code entered` with backoff retries (correct before pairing). |
| E/F/G | **PC1 → PC2 over Wi-Fi** | Job printed on PC1 (`Out-Printer`) while unpaired was held by the installed client. After the receiver was installed on PC2 and its pairing code entered on PC1: discovery found `PROGRAMMING@192.168.1.3` (10:34), marked it *paired* and auto-selected it (10:36:11); the job was delivered and **VALIDATED** at 10:36:29 and saved in test mode on PC2 (`received_jobs\20261105_103629_document_976f911a.pdf`). Opening the PDF on PC2 was done by the user. |
| — | Retry after pairing | Integration: entering the pairing code clears the retry backoff so waiting jobs go out at once (fix added after the cross-PC test, where the job waited ~18 s). Shipped in 1.0.1. |
| — | Client upgrade 1.0.0 → 1.0.1 (PC1) | Installer closed the running client, kept 1.0.0 as `Client.previous`, installed 1.0.1, Apps & features shows 1.0.1; pairing code, selected receiver and outbox preserved; the new build started and delivered a job to PC2 immediately. |
| H | PC1 → PC2 → **Canon LBP2900 (CAPT) — physical page** | 11:00 upgrade-check page (`Out-Printer`, 1 page, portrait text) from PC1 → PC2 in print mode: receiver rendered it for *Canon LBP2900*, its Windows spooler reported COMPLETED, and **the user confirmed the page printed correctly**. Scope: one portrait text page. Multi-page, graphics/CAD drawings, landscape, copies and other paper sizes on the Canon are still to be tested. |
| — | Packaging | `Package.ps1` produced self-contained Client and Receiver zips with SHA-256 sums; packaged `nexora.exe` transfer test and packaged GUI start-up passed on PC1. |

## Failed

None in the final run. During development an intermittent integration failure was traced to the **test harness**
(random free TCP and UDP ports could coincide; the receiver correctly rejects equal ports) and fixed. Two
product bugs found by the tests were fixed: refusal errors lost to a TCP reset (`NOT_ALLOWED` showed as
`UNREACHABLE`), and a pairing-code change not applied to the running receiver.

## Not tested

| Item | Reason / next step |
|---|---|
| Large documents and real application jobs (Word/Excel/CAD) PC1 → PC2 over Wi-Fi | Only the one-page install check has crossed the network so far. |
| Receiver installer output (firewall rules, network profile warning) | Run by the user on PC2; its output was not reviewed here. Discovery and TCP worked, so the rules (or a Private profile) allowed traffic. |
| Receiver upgrade; Client/Receiver **rollback and uninstall** | Client upgrade passed (above); the rest not run, as it would disturb the working installs. |
| Windows Firewall rules created by the receiver installer | Needs PC2 install. |
| AutoCAD, SolidWorks | Not installed on PC1. |
| Siemens NX 12 / NX 10 | Installed on PC1 but not yet printed from; must be tested interactively (File → Print → Nexora Printer LAN). |
| PDF reader printing (Acrobat/Edge) | Not run; equivalent GDI PDF path tested. |
| Interactive clicking through every GUI button | Pages rendered and bound to live data; buttons are thin wrappers over tested engine calls, but not clicked by automation. |
| Multi-user / fast-user-switching on PC1 | Not tested. |
| Windows Protected Print Mode | Not enabled on PC1. |

## Requires physical printer

| Item | Status |
|---|---|
| Canon LBP2900 via CAPT driver on PC2 | One portrait text page printed correctly (see Passed, H). Still to test on the device: multi-page jobs, Word/Excel/NX drawings, landscape, copies > 1, Letter/Legal paper, printer-offline → reconnect behaviour. |
| Real paper output, physical page size/margins, copies on device | Not tested. |
| Offline/online transitions of a USB printer | Status mapping implemented from spooler flags; not observed with real hardware. |

## Requires signed driver

Nothing in this release. The architecture uses Microsoft's signed inbox driver, so no driver signing, test-signing
or signature-enforcement change is needed. Only a future custom driver (arbitrary custom paper sizes) would need
Microsoft Hardware Dev Center signing.

## Requires manual Windows configuration

| Item | Where |
|---|---|
| Creating the print queue (UAC approval) | PC1 — done once with approval on 2026-11-05 |
| PC2's network profile must be **Private** for the firewall rules to apply (installer warns; never changes it) | PC2 |
| Canon CAPT driver installation and a Windows test page | PC2 |
| Stop legacy `receiver.py` / `test_receiver.py` (they hold UDP 9101 / TCP 9102) | PC2 |
| Leftover hidden `EXCEL.EXE` (PID 17144) from the Excel automation test on PC1 | Close it via Task Manager; automatic termination was not permitted |
