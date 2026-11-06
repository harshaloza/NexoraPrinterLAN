import json, socket, struct
from datetime import datetime
from pathlib import Path

HOST, PORT = "0.0.0.0", 9102
ALLOWED_PREFIX = "192.168.0."
MAX_JOB = 100 * 1024 * 1024
OUT = Path(__file__).resolve().parent / "received_jobs"

def exact(conn, n):
    data = bytearray()
    while len(data) < n:
        part = conn.recv(min(1024 * 1024, n - len(data)))
        if not part:
            raise ConnectionError("Connection ended before transfer completed")
        data.extend(part)
    return bytes(data)

def main():
    OUT.mkdir(exist_ok=True)
    print(f"Nexora TEST receiver listening on TCP {PORT}")
    print(f"Allowed client subnet: {ALLOWED_PREFIX}0/24")
    print(f"Saving files to: {OUT}")
    print("TEST MODE: files are saved only; nothing is printed.")
    with socket.socket() as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((HOST, PORT))
        server.listen(8)
        while True:
            conn, addr = server.accept()
            with conn:
                try:
                    if not addr[0].startswith(ALLOWED_PREFIX):
                        raise PermissionError("Client is outside allowed LAN")
                    conn.settimeout(60)
                    hlen = struct.unpack("!I", exact(conn, 4))[0]
                    if not 1 <= hlen <= 16384:
                        raise ValueError("Invalid metadata length")
                    meta = json.loads(exact(conn, hlen).decode("utf-8"))
                    size = int(meta["size"])
                    if not 0 <= size <= MAX_JOB:
                        raise ValueError("Job exceeds 100 MB limit")
                    name = Path(str(meta.get("filename", "job.bin"))).name
                    stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
                    target = OUT / f"{stamp}_{name}"
                    remaining = size
                    with target.open("wb") as f:
                        while remaining:
                            chunk = conn.recv(min(1024 * 1024, remaining))
                            if not chunk:
                                raise ConnectionError("Incomplete job transfer")
                            f.write(chunk)
                            remaining -= len(chunk)
                    conn.sendall(json.dumps({"ok": True, "saved_as": target.name, "bytes": size}).encode() + b"\n")
                    print(f"RECEIVED {addr[0]}: {target.name} ({size} bytes)")
                except Exception as e:
                    print(f"FAILED {addr[0]}: {e}")
                    try:
                        conn.sendall(json.dumps({"ok": False, "error": str(e)}).encode() + b"\n")
                    except OSError:
                        pass

if __name__ == "__main__":
    main()
