#!/usr/bin/env python3
"""Discover Nexora Printer LAN receivers on the local broadcast network."""
import socket, json, time
MAGIC=b'NXDISC1'; PORT=9101
s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
s.setsockopt(socket.SOL_SOCKET,socket.SO_BROADCAST,1)
s.bind(('',0)); s.settimeout(.5)
print('Searching for Nexora receivers for 4 seconds...')
s.sendto(MAGIC,('255.255.255.255',PORT))
found={}; end=time.time()+4
while time.time()<end:
    try:
        data,addr=s.recvfrom(4096); info=json.loads(data.decode())
        if info.get('protocol')=='NexoraDiscovery/1': found[addr[0]]=info
    except socket.timeout: pass
    except Exception as e: print('Ignoring invalid reply:',e)
s.close()
if not found:
    print('No receiver found. Check receiver is running and UDP 9101 is allowed on Private network.')
else:
    for ip,info in found.items():
        print('\nNexora receiver found')
        print('Server:',info.get('server_name'))
        print('Address:',ip+':'+str(info.get('tcp_port')))
        print('Printer:',info.get('printer_name'))
        print('Status:',info.get('status'))
