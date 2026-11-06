import json, socket, struct, sys

HOST = sys.argv[1] if len(sys.argv) > 1 else "192.168.0.31"
PORT = 9102

def sample_pdf():
    stream = b"BT /F1 20 Tf 72 730 Td (NEXORA LAN TEST JOB) Tj /F1 12 Tf 0 -32 Td (Transport test only - no physical printer required.) Tj 0 -22 Td (If this PDF arrives intact, the LAN transfer passed.) Tj ET"
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        b"<< /Length " + str(len(stream)).encode() + b" >>\nstream\n" + stream + b"\nendstream",
    ]
    out, offsets = bytearray(b"%PDF-1.4\n"), [0]
    for i, obj in enumerate(objs, 1):
        offsets.append(len(out))
        out.extend(f"{i} 0 obj\n".encode() + obj + b"\nendobj\n")
    xref = len(out)
    out.extend(f"xref\n0 {len(objs)+1}\n".encode())
    out.extend(b"0000000000 65535 f \n")
    for off in offsets[1:]:
        out.extend(f"{off:010d} 00000 n \n".encode())
    out.extend(f"trailer\n<< /Size {len(objs)+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode())
    return bytes(out)

def main():
    payload = sample_pdf()
    header = json.dumps({"filename": "Nexora_LAN_Test.pdf", "size": len(payload)}).encode()
    print(f"Connecting to {HOST}:{PORT} ...")
    with socket.create_connection((HOST, PORT), timeout=10) as s:
        s.settimeout(60)
        s.sendall(struct.pack("!I", len(header)) + header + payload)
        response = s.makefile("rb").readline()
    result = json.loads(response.decode())
    if not result.get("ok"):
        raise SystemExit(f"Receiver error: {result}")
    print("SUCCESS: test job received.")
    print(f"Saved file: {result['saved_as']}")
    print(f"Bytes transferred: {result['bytes']}")
    print("Open the saved PDF on PC2 to verify it.")

if __name__ == "__main__":
    main()
