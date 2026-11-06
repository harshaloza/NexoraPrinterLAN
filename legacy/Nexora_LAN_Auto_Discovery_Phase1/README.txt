NEXORA PRINTER LAN - AUTO DISCOVERY PHASE 1

This phase adds automatic LAN discovery. It is not yet the Windows virtual printer driver.

Your current network:
  Printer/server PC: 192.168.1.4
  Client PC:          192.168.1.7
  Printer queue:     Canon LBP2900
  TCP print port:    9100
  UDP discovery:     9101

SERVER PC:
1. Back up your current receiver.py.
2. Copy this package's receiver.py to a new folder, e.g. C:\Nexora\Phase1.
3. Run: py -3 receiver.py
4. Keep the window open.

CLIENT PC:
1. Copy discover_server.py to a folder.
2. Run: py -3 discover_server.py
Expected: receiver address 192.168.1.4:9100 and printer Canon LBP2900.

FIREWALL (only if discovery is blocked): Run as Administrator on server:
New-NetFirewallRule -DisplayName "Nexora Discovery UDP 9101" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 9101 -Profile Private -RemoteAddress 192.168.1.0/24
New-NetFirewallRule -DisplayName "Nexora Print TCP 9100" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 9100 -Profile Private -RemoteAddress 192.168.1.0/24
Do not enable all firewall profiles or expose these ports to the Internet.

IMPORTANT:
- Receiver accepts clients from 192.168.1.0/24. Edit ALLOWED if your trusted LAN differs.
- The TCP protocol is NXPR + 4-byte little-endian name length + UTF-8 name + payload.
- Payload is submitted as RAW. It must already be printer-ready data; this does not render arbitrary application documents.
- Canon LBP2900 uses Canon CAPT technology. We must validate real driver output before claiming end-to-end compatibility.
- Do not install/register the experimental NexoraPortMon.dll yet.
