# Nexora Printer LAN — Network protocol (NXP/2 and NexoraDiscovery/2)

Version 2. Implemented in `src/Transport` and `src/Discovery`. The earlier prototype protocols (`NXPR` raw on
TCP 9100, `NXDISC1`, and the JSON-header test protocol on 9102) are not compatible and are retired.

## 1. Ports

| Port | Proto | Purpose | Default |
|---|---|---|---|
| Discovery | UDP | broadcast request / unicast reply | 9101 |
| Transport | TCP | jobs, status, info | 9102 |

Both are configurable on both sides. TCP 9100 (used by the first prototype and by many network printers for RAW
printing) is deliberately not used.

## 2. Keys and authentication

The receiver generates a **pairing code**: 20 Crockford-base32 characters (100 bits), shown as
`XXXX-XXXX-XXXX-XXXX-XXXX`. The user enters it on each client. Both store it DPAPI-protected. The code itself is
never transmitted.

```
secret        = SHA-256("nexora-pairing-v1:" + canonical code)
auth_key      = HKDF-SHA256(secret, salt = "", info = "nexora nxp2 auth")
discovery_key = HKDF-SHA256(secret, salt = "", info = "nexora nxp2 discovery")
c2s_key, s2c_key = HKDF-SHA256(secret, salt = client_nonce || server_nonce, info = "nexora nxp2 c2s" / "... s2c")
```

Handshake (plaintext frames, JSON bodies):

| # | Dir | Frame | Body |
|---|---|---|---|
| 1 | C→S | HELLO 0x01 | `{protocol:"NXP", version:2, clientId, clientName, nonce: b64(32 bytes)}` |
| 2 | S→C | HELLO_REPLY 0x02 | `{version, receiverId, receiverName, nonce: b64(32), proof: b64(HMAC(auth_key, "srv"‖cN‖sN‖receiverId))}` |
| 3 | C→S | AUTH 0x03 | `{proof: b64(HMAC(auth_key, "cli"‖sN‖cN‖clientId))}` |
| 4 | S→C | AUTH_OK 0x04 *(first encrypted frame)* | `{receiverId, receiverVersion, testMode, maxJobBytes, chunkSize}` |

The client verifies the server proof before sending its own, so a rogue receiver without the code learns nothing.
A wrong code ends the session with `ERROR {code:"AUTH_FAILED"}`.

## 3. Framing

```
"N" "X" | type (1 byte) | reserved (0) | length (uint32 big-endian) | payload (length bytes, ≤ 1 MiB + 64)
```

After AUTH_OK every payload is `AES-256-GCM(key, nonce, plaintext, aad)` with
`nonce = direction (u32 BE: 1 = client→server, 2 = server→client) ‖ sequence (u64 BE)` and
`aad = type ‖ sequence`. Sequence numbers start at 0 per direction and increase by one per frame, so tampering,
re-ordering, replay and changing the frame type are detected (`AUTH_FAILED`, connection closed).

| Type | Name | Body |
|---|---|---|
| 0x10 | JOB_OFFER | JobMetadata (below) |
| 0x11 | OFFER_REPLY | `{decision: accept\|duplicate\|reject, resumeOffset, existing?: JobStatus, errorCode?, message?}` |
| 0x12 | DATA | raw bytes, ≤ chunkSize (256 KiB) |
| 0x13 | DATA_END | `{totalBytes}` |
| 0x14 | JOB_RESULT | `{status: JobStatus}` |
| 0x20 / 0x21 | STATUS_QUERY / STATUS_REPLY | `{jobIds:[…]}` (≤500) / `{jobs:[JobStatus]}` |
| 0x22 / 0x23 | CANCEL / CANCEL_REPLY | `{jobId}` / `{cancelled, status?, message}` |
| 0x30 / 0x31 | INFO_QUERY / INFO_REPLY | empty / `{receiverId, receiverName, version, testMode, destinationPrinter, printers:[…], pendingJobs}` |
| 0x7F | ERROR | `{code, message}` (plaintext before AUTH_OK, encrypted after) |

### JobMetadata (JSON, camelCase)

```json
{
  "jobId": "uuid", "documentName": "Microsoft Word - Report", "format": "application/pdf",
  "sizeBytes": 186924, "sha256": "64 lowercase hex", "pageCount": 2,
  "settings": { "paperSize": "A4", "paperWidthTenthMm": 2100, "paperLengthTenthMm": 2970,
                "orientation": "portrait|landscape|unspecified", "colorMode": "color|monochrome|unspecified",
                "copies": 1, "collate": true, "resolutionDpi": 600, "duplex": "Simplex" },
  "createdUtc": "ISO-8601", "clientId": "…", "clientName": "PC1", "submittedBy": "user"
}
```

### JobStatus

`{jobId, state, errorCode?, message?, printerName?, isFinal, updatedUtc}`; `state` is one of
`QUEUED TRANSFERRING RECEIVED VALIDATED SUBMITTED_TO_PRINTER PRINTING COMPLETED FAILED CANCELLED`.
`isFinal` = the receiver will not change the job on its own (terminal, or saved in test mode).

## 4. Job transfer

1. Client sends JOB_OFFER. The receiver validates: client id = authenticated id, `format = application/pdf`,
   size 1..maxJobBytes, SHA-256 syntax, name length, copies 1..999.
2. OFFER_REPLY:
   * `reject` with `UNSUPPORTED_FORMAT`, `TOO_LARGE`, `BAD_REQUEST`, `BUSY` or `STORAGE_ERROR`;
   * `duplicate` + existing status when that `jobId` was already received (no bytes are resent);
   * `accept` + `resumeOffset` — non-zero when a partial upload with the same id, size, hash and client exists.
3. Client streams DATA from `resumeOffset`, then DATA_END.
4. Receiver flushes the file to disk, **re-reads and hashes it**, checks PDF structure, moves it to
   `received_jobs`, routes it (test mode or print queue) and answers JOB_RESULT. Checksum mismatch →
   `FAILED/CHECKSUM_MISMATCH` (partial data deleted; the client resends automatically). Not a PDF →
   `FAILED/INVALID_DOCUMENT` (quarantined in `rejected/`, never in `received_jobs`).
5. The client polls STATUS_QUERY until `isFinal`.

Timeouts: connect 5 s, handshake 15 s, idle frame 60 s, whole transfer 30 min (configurable), verification
≥ 60 s scaled by size. Retries: 5 s, 10 s, 20 s … capped at 5 min, for 24 h (configurable), always with the
same `jobId`, so retries can never print twice.

## 5. Discovery

Request (UDP broadcast to 255.255.255.255 and each interface's directed broadcast, sent twice):

```json
{"magic":"NXDISC2","v":2,"nonce":"b64(16 bytes)","clientId":"…"}     (≤ 512 bytes)
```

Reply (unicast to the sender):

```json
{"body":{"protocol":"NexoraDiscovery/2","version":2,"receiverId":"…","serverName":"HarshalOza-PC","tcpPort":9102,
         "appVersion":"1.0.0","testMode":true,"status":"ready|no-printer|printer-offline|busy",
         "destinationPrinter":"Canon LBP2900","destinationPrinterReady":false,"nonce":"<echo>"},
 "tag":"b64(HMAC(discovery_key, nonce ‖ raw body bytes))"}
```

The client accepts replies only with its own nonce. A reply whose tag verifies under the client's pairing code is
**trusted (paired)**. Auto-selection: exactly one trusted compatible receiver → selected; otherwise the remembered
receiver id if trusted; otherwise the user chooses. The receiver's address is taken from the UDP source address,
so DHCP changes are followed automatically. The responder ignores requests from non-allowed addresses and limits
each address to 20 requests/second.

## 6. Error codes

| Code | Meaning | Client action |
|---|---|---|
| `PROTOCOL_VERSION` | incompatible version | fail permanently |
| `BAD_REQUEST` | malformed or inconsistent message | fail permanently |
| `AUTH_FAILED` | wrong pairing code or tampered frame | keep queued, show error, retry |
| `NOT_ALLOWED` | client address outside allowed networks | keep queued, show error |
| `TOO_LARGE` / `UNSUPPORTED_FORMAT` / `INVALID_DOCUMENT` | job not acceptable | fail permanently |
| `CHECKSUM_MISMATCH` | bytes on disk differ from client hash | resend automatically |
| `STORAGE_ERROR` / `BUSY` / `TIMEOUT` / `UNREACHABLE` / `NO_RECEIVER` | transient | retry with backoff |
| `PRINTER_UNAVAILABLE` / `PRINT_FAILED` | receiver could not print (file is kept) | shown; reprint on receiver |
| `NOT_FOUND` | receiver has no record | shown |
| `CANCELLED` | cancelled by user | final |
