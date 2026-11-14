<#
.SYNOPSIS
  Builds a self-contained, single-file CodexBar.exe that needs no .NET install.
.EXAMPLE
  ./Scripts/publish.ps1                 # win-x64 into ./publish
  ./Scripts/publish.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Output = (Join-Path $PSScriptRoot '..\publish')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\CodexBar.App\CodexBar.App.csproj'

dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Published: $(Join-Path (Resolve-Path $Output) 'CodexBar.exe')"
