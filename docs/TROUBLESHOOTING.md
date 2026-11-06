# Nexora Printer LAN — Troubleshooting

Start with the read-only check:
```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\Nexora Printer LAN\scripts\Diagnostics.ps1" -Receiver <PC2 address>
```
Logs: `%LOCALAPPDATA%\Nexora\Client\logs` and `%LOCALAPPDATA%\Nexora\Receiver\logs` (JSON lines; *Settings → Open log folder*).

| Symptom | Likely cause | Fix |
|---|---|---|
| "Nexora Printer LAN" missing from print dialogs | Queue not installed | Client → Printers → *Add printer…* (UAC) or `Install-NexoraQueue.ps1` as admin |
| Job stays in the Windows queue as *Error* | Nexora Printer LAN not running (port cannot open) | Start Nexora Printer LAN; it restarts stalled jobs automatically |
| Dashboard: *Job capture: Cannot create pipe … Is another Nexora Client already running?* | Another copy is capturing (an old 1.0.x NexoraClient.exe, or `nexora client` CLI) | Exit the other copy; installing 1.1 Setup removes 1.0.x |
| Receiver pages missing / client pages missing | That role is not installed on this PC | Apps & features → Nexora Printer LAN → Modify, choose the role, restart the app |
| Setup: *A newer version … is already installed* | Downgrade attempt | Uninstall first, then install the older version |
| Captured job fails with *non-PDF data* | Queue driver was changed | Repair printer (Printers page) — driver must be *Microsoft Print To PDF* |
| Devices page empty | Receiver not running; UDP 9101 blocked; PC2 network is **Public**; PCs on different subnets; old `receiver.py` still holds UDP 9101 | Run Diagnostics on PC2; set network to Private; stop old receivers; or use *Manual receiver address* |
| Receiver listed as *Not paired* | Different pairing code | Re-enter the code from the receiver dashboard on the client |
| Job error `AUTH_FAILED` | Wrong/regenerated pairing code | Re-enter the code; job retries automatically |
| Job error `NOT_ALLOWED` | Client address not in receiver's allowed networks | Receiver Settings → Allowed clients (empty = local subnet) |
| Job error `UNREACHABLE` / retrying | PC2 off, receiver closed, firewall, IP changed | Client rediscovers by receiver id automatically when discovery works; check `Test-NetConnection <PC2> -Port 9102` |
| Receiver: *TCP port 9102 is already in use* | Phase 2 `test_receiver.py` running | Stop it (Ctrl+C) and click *Start receiver* |
| Job *Failed — PRINTER_UNAVAILABLE* | Test mode off but no / missing destination printer | Receiver → Printers → choose printer; the document is still saved and can be reprinted |
| Job stays *Submitted to printer — Waiting in Windows queue: Offline* | Printer disconnected/off (e.g. LBP2900 unplugged) | Connect and power on the printer; Windows prints the waiting job. Check the printer's own queue on PC2 |
| Output scaled slightly smaller than original | Default *fit to printable area* (laser printers cannot print to the edge) | Receiver Settings → *Print at actual size* (content in the margin will be clipped) |
| Paper size wrong on PC2 | Destination driver has no matching paper (±6 mm) | Add/choose the paper in the printer's driver; the job uses the driver default otherwise |
| Two copies printed per copy | Application put copies in the PDF *and* in the job ticket (not observed so far) | Print 1 copy in the application, report the application |
| SmartScreen warns on install | Unsigned MSI | *More info → Run anyway*, or sign during packaging (INSTALLATION §9) |
| Receiver's *Open/Folder/Print* buttons are disabled for a printed job | The copy was deleted after printing (Keep copies of printed jobs is off) | Turn the setting on for future jobs, or print again from the client |

## Canon LBP2900 (CAPT) notes

* Install Canon's CAPT driver for your Windows version and confirm a Windows test page prints **before** selecting
  it in Nexora.
* CAPT is a host-based driver; Nexora uses it exactly like any desktop application (GDI), so no special mode is
  needed. Do not point Nexora at the printer port directly — RAW PDF/XPS data cannot be printed by CAPT devices.
* When the USB cable is unplugged, Windows marks the queue offline; jobs wait in Windows' queue and the Nexora
  status says so.
