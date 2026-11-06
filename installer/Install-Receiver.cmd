@echo off
rem Nexora Printer LAN - Receiver installer launcher. Windows asks for administrator approval (UAC).
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0Install-NexoraReceiver.ps1\"'"
