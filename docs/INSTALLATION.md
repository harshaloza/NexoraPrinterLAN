# Nexora Printer LAN — Installation

One Setup file serves both PCs: **`NexoraPrinterLAN-<version>-x64.msi`** (built by `scripts\Package.ps1` into `release\`).
During Setup you choose what each PC does:

| Role (Setup feature) | Choose it on | What it adds |
|---|---|---|
| **Print to another PC** | the PC you print from (PC1) | the printer **Nexora Printer LAN** with Nexora's own driver (jobs sent as XPS, no PDF) on local port `\\.\pipe\NexoraPrinterLAN`; the Nexora driver-signing certificate is added to the PC's trusted certificates. Falls back to Windows' *Microsoft Print To PDF* driver if the Nexora driver cannot be installed |
| **Receive and print on this PC** | the PC with the physical printer (PC2) | inbound firewall rules TCP 9102 / UDP 9101 — Private networks, local subnet, `NexoraPrinterLAN.exe` only |

A PC can have both roles. Requirements: Windows 10 2004 (build 19041) or newer, x64, administrator approval.
.NET is **not** required (the app is self-contained). No port monitor, service or spooler DLL is installed; the Nexora driver consists of text files only (INF, GPD, manifest).

**Upgrade both PCs to the same version.** A 1.2 client sends XPS jobs, which a 1.0/1.1 receiver rejects (*format not supported*). A 1.2 receiver still accepts PDF jobs from older clients.

## 1. Install on the printer PC (PC2) first

1. Copy the `.msi` to PC2 and double-click it.
2. *Welcome → License → Custom Setup*: open the drop-down next to **Receive and print on this PC** and choose
   *Will be installed on local hard drive*. Set **Print to another PC** to *Entire feature will be unavailable*
   (unless PC2 should also print to another PC). *Next → Install*, approve UAC, keep **Start Nexora Printer LAN now** ticked.
3. Nexora Printer LAN shows the **pairing code**. The receiver starts in **test mode** (documents are saved,
   not printed).
4. If PC2's network is marked **Public**, the firewall rules do not apply. On a trusted LAN change it to Private:
   *Settings → Network & internet → Wi-Fi (or Ethernet) → your network → Network profile type → Private*.

Command line (PowerShell as Administrator on PC2):
```powershell
msiexec /i .\NexoraPrinterLAN-1.2.2-x64.msi ADDLOCAL=Receiver /qb
```

## 2. Install on the PC you print from (PC1)

1. Run the same `.msi`; the default selection (**Print to another PC**) is right. Install, approve UAC.
2. In Nexora Printer LAN → **Settings → Printing to another PC → Pairing code**, type the code from PC2 and click
   *Save pairing code*.
3. **Devices** → the PC2 receiver appears as *Paired* and is selected automatically. If discovery is blocked, set
   *Manual receiver address* to PC2's IP or name.
4. **Dashboard → Send test page.** It should reach *Validated* (test mode) on PC2.

Command line (PowerShell as Administrator on PC1):
```powershell
msiexec /i .\NexoraPrinterLAN-1.2.2-x64.msi ADDLOCAL=Client /qb
```

## 3. Print

From any application choose **Nexora Printer LAN** → Print. Jobs appear under *Print jobs* (PC1) and *Received
jobs* (PC2).

## 4. Start real printing on PC2

1. Install the printer's own Windows driver (Canon LBP2900: Canon **CAPT** driver) and print a Windows test page
   from *Printers & scanners*. Nexora never modifies the printer driver.
2. Nexora Printer LAN → **Printers on this PC** → select the printer → *Use as destination* → *Print test page*.
3. Dashboard → **Test mode OFF (print)**.
4. By default the received PDF is deleted once Windows has sent the job to the printer. To keep copies:
   Settings → *Keep copies of printed jobs*.

## 5. Change roles later (Modify)

*Settings → Apps → Installed apps → Nexora Printer LAN → ⋯ → Modify* (or run the `.msi` again → *Change*), then
pick the roles. Or, as Administrator:
```powershell
msiexec /i .\NexoraPrinterLAN-1.2.2-x64.msi ADDLOCAL=Receiver /qb    # add a role
msiexec /i .\NexoraPrinterLAN-1.2.2-x64.msi REMOVE=Receiver /qb      # remove a role
```
Restart Nexora Printer LAN (tray → Exit, then start it) after changing roles.

## 6. Upgrade

Run the newer `.msi`. Setup closes the running app, replaces it, and keeps the installed roles, settings, pairing
codes and job history (`%LOCALAPPDATA%\Nexora`). Installing 1.1 over the separate **Nexora Client / Nexora
Receiver 1.0.x** apps removes those (program folders, autostart, Apps & features entries, old firewall rules),
pre-selects the matching roles and keeps the print queue and all data.

Downgrades are refused. To go back to an older version, uninstall and install the older `.msi`.

## 7. Uninstall

*Settings → Apps → Installed apps → Nexora Printer LAN → Uninstall*, Start menu → **Uninstall Nexora Printer LAN**,
or as Administrator:
```powershell
msiexec /x .\NexoraPrinterLAN-1.2.2-x64.msi /qb
```
This removes the app, the *Nexora Printer LAN* printer and its port, the firewall rules, autostart and shortcuts.
Settings, pairing codes, job history and received documents in `%LOCALAPPDATA%\Nexora` are kept; delete that
folder to remove them too.

## 8. Checks and logs

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\Nexora Printer LAN\scripts\Diagnostics.ps1" -Receiver <PC2 address>
& "C:\Program Files\Nexora Printer LAN\nexora.exe" discover --key XXXX-XXXX-XXXX-XXXX-XXXX
& "C:\Program Files\Nexora Printer LAN\nexora.exe" printers
```
Setup log: run `msiexec /i <msi> /l*v setup.log`. App logs: `%LOCALAPPDATA%\Nexora\Client\logs`, `…\Receiver\logs`.

## 9. Signing

* **The Nexora driver package is signed** (its catalog) by `scripts\Package.ps1`. By default it uses the local
  certificate *Nexora Printer LAN Driver Signing (local)*, created on the build PC with a non-exportable private key;
  Setup adds its public part to the target PC's *Trusted Root* and *Trusted Publishers* so Windows accepts the
  driver. That is suitable for PCs you manage. For PCs you do not manage, sign with a purchased code-signing
  certificate (`-SignCertThumbprint`); no certificate is then added to the PC. Uninstall removes the certificate.
  Test-signing mode and driver-signature enforcement are never changed.
* The `.msi` and executables are unsigned by default, so Windows SmartScreen may warn when a downloaded copy is
  opened (*More info → Run anyway*). To sign during packaging with a code-signing certificate:
  `.\scripts\Package.ps1 -SignCertThumbprint <thumbprint>`.

## 10. Building from source

```powershell
.\scripts\Build.ps1          # debug build
.\scripts\Test.ps1           # unit + integration tests
.\scripts\Package.ps1        # tests, single-file publish, release\NexoraPrinterLAN-<ver>-x64.msi + SHA256SUMS.txt
```
Packaging uses the WiX 4 command-line tool (`dotnet tool install --global wix --version 4.0.5`) and its UI, Util
and Firewall extensions (`wix extension add WixToolset.UI.wixext/4.0.5` etc., run in `installer\msi`).
