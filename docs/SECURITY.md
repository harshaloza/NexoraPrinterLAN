# Nexora Printer LAN — Security

Nexora is designed for a **trusted local network**. It is not meant to be reachable from the Internet and refuses
to work across it.

## Controls

| Area | Control | Where |
|---|---|---|
| Network exposure | Receiver refuses any non-private IPv4 address (only 10/8, 172.16/12, 192.168/16, 169.254/16), even if a firewall rule is wrong. Default allow-list = this PC's own directly-connected subnets; can be narrowed to explicit CIDRs/addresses. | `AddressFilter`, receiver Settings |
| Firewall | Installer adds two inbound rules only: program-scoped to `NexoraReceiver.exe`, **Private profile only**, remote address `LocalSubnet` (or a CIDR you pass). No Public/Domain rules, no "Any" address, no router port forwarding. The client needs no inbound rule. The installer never changes the network profile. | `installer/Install-NexoraReceiver.ps1` |
| Authentication | Mutual HMAC-SHA256 challenge/response from a 100-bit pairing code; fresh 256-bit nonces from both sides prevent replay. | `Handshake`, `KeySchedule` |
| Confidentiality / integrity | Every frame after the handshake is AES-256-GCM with per-direction keys, sequence-number nonces and authenticated frame type. Documents are encrypted on the LAN. | `SecureChannel` |
| Discovery spoofing | Replies are HMAC-tagged with a key derived from the pairing code; only tagged receivers are "paired" and auto-selected. Requests carry a random nonce that must be echoed. | `DiscoveryCodec` |
| Secrets at rest | Pairing codes stored with Windows DPAPI (CurrentUser scope + app entropy); never in plain text, never logged (only an 8-hex fingerprint). | `SecretProtector` |
| Input validation | Frame magic, type and length (≤ 1 MiB) checked before allocation; JSON parsed into typed records only (no polymorphic type names, max depth 16); job size limit (default 512 MB); format must be `application/pdf`; SHA-256 recomputed from disk; PDF header/trailer checked; copies 1..999; client id must match the authenticated id. | `FrameCodec`, `ReceiverServer`, `JobStore` |
| File handling | Document names are reduced to a single safe file-name component (no paths, reserved device names, control characters); stored files are named `<timestamp>_<name>_<id>.pdf` inside the receiver's own folder; temporary uploads live in `incoming/`; invalid uploads go to `rejected/` as `.bin`. Writes use temp-file + flush + rename. | `SafeFileName`, `AtomicFile` |
| Execution | Received data is never executed, opened automatically or sent RAW to a printer. Printing renders the PDF with Windows' own PDF engine and draws through the printer driver. No shell commands are built from network input. | `GdiPdfPrinter` |
| Local capture pipe | ACL: SYSTEM and the owner full control, Authenticated Users read/write, **Network logons denied** (not reachable over SMB). `FirstPipeInstance` prevents another process from pre-creating the pipe while the client runs. | `PipeCaptureServer` |
| Denial of service | Max concurrent connections (8), idle timeout 60 s, transfer timeout 30 min, discovery rate limit 20/s per address, partial uploads purged after 2 days, retention purge (30 days receiver, 14 days client). | `ReceiverConfig`, `ClientConfig` |
| Logging | Structured JSON logs record names, sizes, IDs, states and addresses — never document contents or secrets. | `Log` |
| Installation | No driver, port monitor or DLL is installed into the spooler; no Windows security feature is disabled; UAC is required for queue and firewall changes. | installers |

## Known limitations

* Anyone who knows the pairing code and is on an allowed subnet can print to the receiver. Regenerate the code on
  the receiver (Dashboard → Generate new code) if it leaks; all clients must then be re-paired.
* Local users on the client PC can write to the capture pipe (as they can print to the queue anyway).
* The client and receiver binaries are not code-signed unless you sign them during packaging
  (`Package.ps1 -SignCertThumbprint`). Unsigned downloads may trigger SmartScreen.
* No per-user authorization on the receiver beyond the pairing code (single-tenant home/office design).
