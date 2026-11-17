<#
.SYNOPSIS
    Installs Bluetooth Battery Tray to "C:\Program Files\BluetoothBatteryTray".

.DESCRIPTION
    - Asks for administrator rights (UAC) if needed
    - Copies the published files to Program Files
    - Creates a Start menu shortcut
    - Starts the app (as the normal user, not as admin)

    Usage:
        powershell -ExecutionPolicy Bypass -File scripts\install.ps1
        powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -SourceDir C:\path\to\published\files
#>
param(
    [string]$SourceDir
)

$ErrorActionPreference = 'Stop'
$AppName    = 'BluetoothBatteryTray'
$ExeName    = "$AppName.exe"
$InstallDir = Join-Path $env:ProgramFiles $AppName
$Shortcut   = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Bluetooth Battery Tray.lnk"

# Find the published files: explicit parameter, next to this script (release zip), or ..\publish (repo).
if (-not $SourceDir) {
    $candidates = @($PSScriptRoot, (Join-Path $PSScriptRoot '..\publish'))
    $SourceDir = $candidates | Where-Object { Test-Path (Join-Path $_ $ExeName) } | Select-Object -First 1
}
if (-not $SourceDir -or -not (Test-Path (Join-Path $SourceDir $ExeName))) {
    Write-Error "Could not find $ExeName. Run 'dotnet publish' first (see README) or pass -SourceDir."
}
$SourceDir = (Resolve-Path $SourceDir).Path

# Re-launch elevated if needed.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Requesting administrator rights...'
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-SourceDir', "`"$SourceDir`"")
    exit
}

# Stop a running instance so its files can be replaced.
Get-Process $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Installing to $InstallDir ..."
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force
# Don't ship the installer scripts inside Program Files.
Get-ChildItem $InstallDir -Filter '*.ps1' | Remove-Item -Force

# Start menu shortcut (all users).
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($Shortcut)
$lnk.TargetPath = Join-Path $InstallDir $ExeName
$lnk.WorkingDirectory = $InstallDir
$lnk.Description = 'Bluetooth battery and now playing on the taskbar'
$lnk.Save()

# Start the app un-elevated (explorer.exe launches it as the logged-in user).
Start-Process explorer.exe -ArgumentList "`"$(Join-Path $InstallDir $ExeName)`""

Write-Host ''
Write-Host 'Installed! Right-click the overlay on the taskbar > "Start with Windows" to launch it automatically.'
Start-Sleep -Seconds 3
