<#
.SYNOPSIS
  Removes CodexBar for the current user.
.DESCRIPTION
  Stops CodexBar, removes the program folder, the Start Menu shortcut and the start-at-login entry.
  Settings and history in %APPDATA%\CodexBar are kept unless -RemoveSettings is passed.
#>
param([switch]$RemoveSettings)

$ErrorActionPreference = 'Stop'
Get-Process -Name CodexBar -ErrorAction SilentlyContinue | Stop-Process -Force

$target = Join-Path $env:LOCALAPPDATA 'Programs\CodexBar'
if (Test-Path $target) { Remove-Item -Path $target -Recurse -Force }

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'CodexBar.lnk'
if (Test-Path $shortcutPath) { Remove-Item -Path $shortcutPath -Force }

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CodexBar' -ErrorAction SilentlyContinue

if ($RemoveSettings) {
    $settings = Join-Path $env:APPDATA 'CodexBar'
    if (Test-Path $settings) { Remove-Item -Path $settings -Recurse -Force }
}

Write-Host 'CodexBar was removed.'
