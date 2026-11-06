#!/usr/bin/env python3
"""Nexora LAN receiver with UDP discovery. Windows only; standard library."""
import ctypes, ctypes.wintypes as wt, socket, struct, threading, json, ipaddress, os, sys, time
TCP_PORT=9100; UDP_PORT=9101; PRINTER_NAME='Canon LBP2900'
MAGIC=b'NXDISC1'; JOB_MAGIC=b'NXPR'; ALLOWED=ipaddress.ip_network('192.168.1.0/24')
MAX_JOB=512*1024*1024; TIMEOUT=120
class DOC_INFO_1W(ctypes.Structure):
    _fields_=[('pDocName',wt.LPWSTR),('pOutputFile',wt.LPWSTR),('pDatatype',wt.LPWSTR)]
def check(ok,what):
    if not ok: raise ctypes.WinError(ctypes.get_last_error(),what)
def submit_raw(name,data):
    w=ctypes.WinDLL('winspool.drv',use_last_error=True); h=wt.HANDLE()
    w.OpenPrinterW.argtypes=[wt.LPWSTR,ctypes.POINTER(wt.HANDLE),ctypes.c_void_p]; w.OpenPrinterW.restype=wt.BOOL
    w.ClosePrinter.argtypes=[wt.HANDLE]; w.ClosePrinter.restype=wt.BOOL
    w.StartDocPrinterW.argtypes=[wt.HANDLE,wt.DWORD,ctypes.POINTER(DOC_INFO_1W)]; w.StartDocPrinterW.restype=wt.DWORD
    w.StartPagePrinter.argtypes=[wt.HANDLE]; w.StartPagePrinter.restype=wt.BOOL
    w.WritePrinter.argtypes=[wt.HANDLE,ctypes.c_void_p,wt.DWORD,ctypes.POINTER(wt.DWORD)]; w.WritePrinter.restype=wt.BOOL
    w.EndPagePrinter.argtypes=[wt.HANDLE]; w.EndPagePrinter.restype=wt.BOOL
    w.EndDocPrinter.argtypes=[wt.HANDLE]; w.EndDocPrinter.restype=wt.BOOL
    w.AbortPrinter.argtypes=[wt.HANDLE]; w.AbortPrinter.restype=wt.BOOL
    check(w.OpenPrinterW(PRINTER_NAME,ctypes.byref(h),None),'OpenPrinter')
    active=False
    try:
        di=DOC_INFO_1W(name,None,'RAW'); jid=w.StartDocPrinterW(h,1,ctypes.byref(di)); check(bool(jid),'StartDocPrinter'); active=True
        check(w.StartPagePrinter(h),'StartPagePrinter')
        buf=ctypes.create_string_buffer(data); n=wt.DWORD(0)
        check(w.WritePrinter(h,buf,len(data),ctypes.byref(n)),'WritePrinter')
        if n.value!=len(data): raise OSError(f'Short write {n.value}/{len(data)}')
        check(w.EndPagePrinter(h),'EndPagePrinter'); check(w.EndDocPrinter(h),'EndDocPrinter'); active=False
        return jid
    finally:
        if active:
            try: w.AbortPrinter(h)
            except Exception: pass
        w.ClosePrinter(h)
def exact(c,n):
    out=bytearray()
    while len(out)<n:
        b=c.recv(min(65536,n-len(out)))
        if not b: raise ConnectionError('Unexpected disconnect')
        out.extend(b)
    return bytes(out)
def handle(c,a):
    c.settimeout(TIMEOUT); print('[TCP] Client:',a[0])
    try:
        if ipaddress.ip_address(a[0]) not in ALLOWED: raise PermissionError('Client outside allowed LAN')
        if exact(c,4)!=JOB_MAGIC: raise ValueError('Invalid Nexora protocol signature')
        n=struct.unpack('<I',exact(c,4))[0]
        if not 1<=n<=1024: raise ValueError('Invalid job name length')
        name=exact(c,n).decode('utf-8','replace'); payload=bytearray()
        while True:
            b=c.recv(65536)
            if not b: break
            payload.extend(b)
            if len(payload)>MAX_JOB: raise ValueError('Job exceeds 512 MB')
        if not payload: print('[JOB] Ignored empty job:',name); return
        print(f"[JOB] '{name}', {len(payload):,} bytes; submitting RAW")
        jid=submit_raw(name,bytes(payload)); print('[JOB] Submitted; Windows job ID:',jid)
    except Exception as e: print('[ERROR]',e)
    finally: c.close()
def discovery():
    s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1); s.bind(('',UDP_PORT))
    print(f'[UDP] Discovery listening on {UDP_PORT}')
    while True:
        try:
            data,a=s.recvfrom(2048)
            if data==MAGIC:
                reply={'protocol':'NexoraDiscovery/1','server_name':socket.gethostname(),'printer_name':PRINTER_NAME,'tcp_port':TCP_PORT,'status':'ready'}
                s.sendto(json.dumps(reply).encode(),a); print('[DISCOVERY] Replied to',a[0])
        except Exception as e: print('[UDP ERROR]',e); time.sleep(1)
def main():
    if os.name!='nt': print('Run this receiver on Windows.'); sys.exit(1)
    print('Nexora Printer LAN - Auto Discovery Phase 1')
    print('Printer queue:',PRINTER_NAME,'| Allowed LAN:',ALLOWED)
    threading.Thread(target=discovery,daemon=True).start()
    s=socket.socket(); s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1); s.bind(('',TCP_PORT)); s.listen(10)
    print(f'[READY] TCP receiver listening on {TCP_PORT}. Keep this window open.')
    while True:
        c,a=s.accept(); threading.Thread(target=handle,args=(c,a),daemon=True).start()
if __name__=='__main__': main()
