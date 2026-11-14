<#
.SYNOPSIS
  Installs CodexBar for the current user. No admin rights needed.
.DESCRIPTION
  Copies CodexBar.exe to %LOCALAPPDATA%\Programs\CodexBar, adds a Start Menu shortcut and starts it.
  Pass the path to a downloaded CodexBar.exe, or run it from a folder that contains one.
.EXAMPLE
  ./install.ps1 -Exe .\CodexBar-0.2.0-win-x64.exe -StartAtLogin
#>
param(
    [string]$Exe,
    [switch]$StartAtLogin
)

$ErrorActionPreference = 'Stop'
if (-not $Exe) {
    $found = Get-ChildItem -Path $PSScriptRoot, (Join-Path $PSScriptRoot '..\publish') -Filter 'CodexBar*.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $found) { throw 'CodexBar.exe not found. Pass -Exe <path>, or run Scripts/publish.ps1 first.' }
    $Exe = $found.FullName
}

$target = Join-Path $env:LOCALAPPDATA 'Programs\CodexBar'
$installed = Join-Path $target 'CodexBar.exe'

# A running copy locks the file; stop it before replacing it.
Get-Process -Name CodexBar -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path $Exe -Destination $installed -Force

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'CodexBar.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installed
$shortcut.WorkingDirectory = $target
$shortcut.Description = 'AI coding-provider usage limits in the notification area'
$shortcut.Save()

if ($StartAtLogin) {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CodexBar' -Value "`"$installed`""
}

Start-Process -FilePath $installed
Write-Host "Installed to $installed and started. Look for the meter icon in the notification area."
