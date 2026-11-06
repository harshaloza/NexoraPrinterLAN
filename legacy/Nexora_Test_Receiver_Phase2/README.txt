NEXORA PRINTER LAN - PHASE 2 TEST RECEIVER

Purpose:
PC1 sends a generated sample PDF to PC2. PC2 saves it in received_jobs.
This is transport-only. It does NOT print and does not require the Canon printer.

Network:
PC1: 192.168.0.140
PC2: 192.168.0.31
TCP port: 9102

Requirements: Python 3 on both PCs; no third-party packages.

PC2 - receiver:
1. Extract this ZIP, e.g. D:\\Nexora_Test_Receiver_Phase2
2. Open PowerShell in the extracted folder.
3. Run: py .\\test_receiver.py
4. In Administrator PowerShell on PC2, allow only this LAN port:
   New-NetFirewallRule -DisplayName "Nexora Phase2 Test Receiver" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 9102 -Profile Private -RemoteAddress 192.168.0.0/24

PC1 - sender:
1. Extract the ZIP.
2. Open CMD or PowerShell in the extracted folder.
3. Run: py .\\send_test_job.py
   Optional explicit server: py .\\send_test_job.py 192.168.0.31

Expected PC1 result: SUCCESS: test job received.
Expected PC2 result: RECEIVED from 192.168.0.140 and a saved filename.

Verify:
On PC2, open received_jobs and open the timestamped Nexora_LAN_Test.pdf.
It should display "NEXORA LAN TEST JOB".

Optional PC1 check:
Test-NetConnection 192.168.0.31 -Port 9102
Expected TcpTestSucceeded: True.

Security:
Receiver accepts only 192.168.0.x clients; firewall rule is limited to Private
profile and 192.168.0.0/24. Do not use router port forwarding. Stop with Ctrl+C.

Milestone scope:
A successful test proves PDF bytes can cross the LAN and be saved intact.
It does NOT yet create a Windows virtual printer queue or capture print jobs
from AutoCAD/SolidWorks/NX. That is the next development stage.
