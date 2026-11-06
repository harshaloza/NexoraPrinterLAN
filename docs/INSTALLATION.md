# Nexora Printer LAN — Installation

Two packages are produced by `scripts\Package.ps1` (folder + `.zip` in `release\`):

| Package | Install on | Contains |
|---|---|---|
| `NexoraPrinterLAN-Client-<ver>-win-x64` | **PC1** — the PC you print from (192.168.0.43 at the time of writing; its address may change) | `NexoraClient.exe`, `nexora.exe`, queue scripts, installer |
| `NexoraPrinterLAN-Receiver-<ver>-win-x64` | **PC2** — the PC with the printer (`HarshalOza-PC`, 192.168.0.31 last known) | `NexoraReceiver.exe`, `nexora.exe`, installer |

Requirements: Windows 10 2004 (build 19041) or newer, x64, administrator approval for installation. .NET is
**not** required (packages are self-contained). The client also needs the Windows feature *Microsoft Print to PDF*
(enabled by default on Windows 10/11).

## 0. Before you start (PC2)

The prototypes from `legacy\` bind the same ports. Stop any running `receiver.py` / `test_receiver.py` window
(Ctrl+C) on PC2 first.

## 1. Install the receiver — on PC2

1. Copy `NexoraPrinterLAN-Receiver-1.0.0-win-x64.zip` to PC2, right-click → *Extract All*.
2. Double-click **`Install-Receiver.cmd`** and approve the UAC prompt.
   Equivalent command (PowerShell **as Administrator** on PC2, in the extracted folder):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-NexoraReceiver.ps1
   ```
   To allow only PC1, scope the firewall to PC1's address or your subnet:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-NexoraReceiver.ps1 -RemoteAddress 192.168.0.0/24
   ```
3. Nexora Receiver opens and shows the **pairing code**. It starts in **test mode** (documents are saved to
   `%LOCALAPPDATA%\Nexora\Receiver\received_jobs`, not printed).
4. If the installer reports that the network is **Public**, the firewall rules do not apply. On a trusted LAN,
   change it to Private: *Settings → Network & internet → Wi-Fi (or Ethernet) → your network → Network profile
   type → Private*. Or, PowerShell as Administrator on PC2:
   ```powershell
   Get-NetConnectionProfile
   Set-NetConnectionProfile -InterfaceAlias 'Wi-Fi' -NetworkCategory Private
   ```

## 2. Install the client — on PC1

1. Extract `NexoraPrinterLAN-Client-1.0.0-win-x64.zip` on PC1.
2. Double-click **`Install-Client.cmd`** and approve UAC. Equivalent (PowerShell as Administrator on PC1):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-NexoraClient.ps1
   ```
   This creates the printer **Nexora Printer LAN** (Settings → Bluetooth & devices → Printers & scanners) using
   the inbox *Microsoft Print To PDF* driver and port `\\.\pipe\NexoraPrinterLAN`, with A4 as default paper
   (`-PaperSize Letter` to change).
3. In Nexora Client → **Settings → Security**, type the pairing code from PC2 and click *Save pairing code*.
4. **Devices** → *Discover*. PC2 should appear as *Paired*. With exactly one paired receiver it is selected
   automatically. If discovery is blocked, enter `192.168.0.31` (or `HarshalOza-PC`) as *Manual receiver address*.
5. **Dashboard** → *Send test page*. The job should reach **Validated** and the PDF appear on PC2.

## 3. Print a real document (PC1)

Print from any application, choose **Nexora Printer LAN**, click Print. The job shows in *Print jobs*
(PC1) and *Received jobs* (PC2).

## 4. Switch PC2 to real printing (when the Canon LBP2900 is connected)

1. Install Canon's **CAPT driver** for the LBP2900 on PC2 and print a Windows test page from *Printers & scanners*
   to confirm the printer itself works. Nexora never modifies the Canon driver.
2. Nexora Receiver → **Printers** → select *Canon LBP2900* → *Use as destination* → *Print test page*.
3. Dashboard → **Test mode OFF (print)**.
4. From PC1, print a one-page document and confirm paper comes out. Only then record the Canon test as passed
   in `docs/TEST_REPORT.md`.

## 5. Command-line checks

PC1 or PC2 (in the extracted package folder):
```powershell
powershell -ExecutionPolicy Bypass -File .\Diagnostics.ps1                       # read-only health check
powershell -ExecutionPolicy Bypass -File .\Diagnostics.ps1 -Receiver 192.168.0.31 # PC1: also test TCP 9102
.\app\nexora.exe discover --key XXXX-XXXX-XXXX-XXXX-XXXX                         # PC1: list receivers, "PAIRED"
.\app\nexora.exe testpdf test.pdf; .\app\nexora.exe send test.pdf --key XXXX-XXXX-XXXX-XXXX-XXXX --to 192.168.0.31
.\app\nexora.exe printers                                                        # PC2: printers + real status
```

## 6. Upgrade and rollback

Run the new package's installer over the old one. The previous program folder is kept as
`C:\Program Files\Nexora Printer LAN\<Client|Receiver>.previous`; settings, pairing codes, outbox and received
documents (in `%LOCALAPPDATA%\Nexora`) are untouched. To go back (as Administrator, from the package folder):
```powershell
powershell -ExecutionPolicy Bypass -File .\Install-NexoraClient.ps1 -Rollback     # PC1
powershell -ExecutionPolicy Bypass -File .\Install-NexoraReceiver.ps1 -Rollback   # PC2
```

## 7. Uninstall

*Settings → Apps → Installed apps → Nexora Printer LAN Client / Receiver → Uninstall*, or as Administrator:
```powershell
& 'C:\Program Files\Nexora Printer LAN\Client\Uninstall-NexoraClient.ps1'          # PC1 (removes queue + port)
& 'C:\Program Files\Nexora Printer LAN\Receiver\Uninstall-NexoraReceiver.ps1'      # PC2 (removes firewall rules)
```
Add `-RemoveData` to also delete settings, outbox/received documents and logs.

## 8. Signing

* **No driver signing is needed**: no driver package is installed; the queue uses Microsoft's signed inbox driver.
  Do not enable test-signing or disable driver signature enforcement for Nexora.
* The executables are unsigned by default. To sign during packaging (code-signing certificate in the current
  user's store):
  ```powershell
  .\scripts\Package.ps1 -SignCertThumbprint <thumbprint>
  ```
  Unsigned packages copied from another PC may show a SmartScreen prompt; the installer unblocks the files it installs.

## 9. Building from source (developer PC)

```powershell
.\scripts\Build.ps1                 # debug build
.\scripts\Test.ps1                  # unit + integration tests
.\scripts\Package.ps1               # tests, self-contained publish, zips + SHA256SUMS.txt in release\
```
For a development run of the queue without the installer (PC1, Administrator):
`powershell -ExecutionPolicy Bypass -File .\scripts\Install-NexoraQueue.ps1`.
