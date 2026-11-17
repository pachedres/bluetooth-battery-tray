<#
.SYNOPSIS
    Removes Bluetooth Battery Tray (program files, Start menu shortcut and auto start entry).
    Settings in %AppData%\BluetoothBatteryTray are kept unless -RemoveSettings is passed.

    Usage:
        powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 [-RemoveSettings]
#>
param(
    [switch]$RemoveSettings
)

$ErrorActionPreference = 'Stop'
$AppName    = 'BluetoothBatteryTray'
$InstallDir = Join-Path $env:ProgramFiles $AppName
$Shortcut   = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Bluetooth Battery Tray.lnk"

# Per-user items first (before elevating, so HKCU/AppData are the real user's).
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $AppName -ErrorAction SilentlyContinue
if ($RemoveSettings) {
    Remove-Item (Join-Path $env:APPDATA $AppName) -Recurse -Force -ErrorAction SilentlyContinue
}
Get-Process $AppName -ErrorAction SilentlyContinue | Stop-Process -Force

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$removeMachineWide = "Remove-Item '$InstallDir' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item '$Shortcut' -Force -ErrorAction SilentlyContinue"

if ($isAdmin) {
    Invoke-Expression $removeMachineWide
} else {
    Write-Host 'Requesting administrator rights...'
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-Command', $removeMachineWide)
}

Write-Host 'Bluetooth Battery Tray was removed.'
