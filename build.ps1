# Copyright (C) 2026 JeepNWilly
# Convertr is free software: you can redistribute it and/or modify it under the terms of the
# GNU General Public License as published by the Free Software Foundation, either version 3
# of the License, or (at your option) any later version. It comes with no warranty.
# See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.
<#
.SYNOPSIS  Builds Convertr.exe: a single-file, win-x64, portable exe, written one level above this folder.
.DESCRIPTION
  Default is SELF-CONTAINED (about 60-70 MB, compressed): runs on any 64-bit Windows 10/11 with nothing installed.
  Use -FrameworkDependent for a small exe (a few MB) that needs the .NET 10 Desktop Runtime installed.
  The unit tests run first; a failing test stops the build. BUILD_NUMBER goes up by one on every build.
.EXAMPLE   powershell -ExecutionPolicy Bypass -File .\build.ps1
.EXAMPLE   powershell -ExecutionPolicy Bypass -File .\build.ps1 -FrameworkDependent -NoTest
#>
param(
    [switch]$FrameworkDependent,
    [switch]$NoTest,
    [switch]$NoBump,
    [string]$OutDir = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

# Find a .NET 10 (or newer) SDK: PATH first, then the per-user install (dotnet-install.ps1 default), then Program Files.
function Find-Dotnet10 {
    $cands = @('dotnet', (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'), (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'), (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    foreach ($c in $cands) {
        try {
            $v = (& $c --version) 2>$null
            if ($v -and ([version](($v -split '-')[0])) -ge [version]'10.0.0') { return $c }
        } catch { }
    }
    return $null
}
$dotnetExe = Find-Dotnet10
if (-not $dotnetExe) { throw 'The .NET 10 SDK was not found. Install it (winget install Microsoft.DotNet.SDK.10, or dotnet-install.ps1 -Channel 10.0) and try again.' }
if ($dotnetExe -ne 'dotnet') {
    $dotnetDir = Split-Path -Parent $dotnetExe
    $env:DOTNET_ROOT = $dotnetDir
    $env:PATH = "$dotnetDir;$env:PATH"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dotnetVersion = (& dotnet --version) 2>$null
Write-Host "dotnet SDK $dotnetVersion"

if (-not $NoTest) {
    Write-Host 'Running unit tests...'
    & dotnet test tests\Convertr.Tests\Convertr.Tests.csproj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed; not building the exe.' }
}

$verFile = Join-Path $PSScriptRoot 'VERSION'
$bnFile  = Join-Path $PSScriptRoot 'BUILD_NUMBER'
$ver = ([IO.File]::ReadAllText($verFile)).Trim()
if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must look like 1.2.3 (got '$ver')" }
$bn = 0
if (Test-Path -LiteralPath $bnFile) { [void][int]::TryParse(([IO.File]::ReadAllText($bnFile)).Trim(), [ref]$bn) }
if (-not $NoBump) { $bn++; [IO.File]::WriteAllText($bnFile, "$bn`r`n") }
Write-Host "Version $ver (build $bn)"

$pub = Join-Path $PSScriptRoot 'publish'
if (Test-Path -LiteralPath $pub) { Remove-Item -LiteralPath $pub -Recurse -Force }
$pargs = @('publish', 'src\Convertr\Convertr.csproj', '-c', 'Release', '-r', 'win-x64', '-o', $pub,
           '-p:PublishSingleFile=true', '-p:DebugType=none', '-p:DebugSymbols=false', '--nologo', '-v', 'q')
if ($FrameworkDependent) {
    $pargs += @('--self-contained', 'false')
} else {
    $pargs += @('--self-contained', 'true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
}
Write-Host ('Publishing (' + $(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }) + ')...')
& dotnet @pargs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$built = Join-Path $pub 'Convertr.exe'
if (-not (Test-Path -LiteralPath $built)) { throw "Convertr.exe was not produced in $pub" }
$dest = Join-Path $OutDir 'Convertr.exe'
try { Copy-Item -LiteralPath $built -Destination $dest -Force }
catch { throw "Could not write $dest. Close Convertr if it is running, then build again. ($($_.Exception.Message))" }
$size = [math]::Round((Get-Item -LiteralPath $dest).Length / 1MB, 1)
$hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash
Write-Host "Built $dest  ($size MB, version $ver.$bn)"
Write-Host "SHA-256 $hash"
