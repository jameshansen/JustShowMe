<#
.SYNOPSIS
    Builds the whole JustShowMe solution (C++ driver + C# GUI + filter) as x64.

.DESCRIPTION
    Locates MSBuild via vswhere (so it works on any machine with a suitable
    Visual Studio install) and builds the solution. All projects output to a
    single distributable folder in the repo root: Debug\ or Release\.

    Requires Visual Studio 2019 or 2022 with:
      - Desktop development with C++  (for the virtual-camera driver)
      - .NET desktop development      (for the WPF GUI / filter)

.PARAMETER Configuration
    Debug (default) or Release.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sln  = Join-Path $root 'JustShowMe.sln'

# Find vswhere (ships with the VS 2017+ installer, always at this fixed path).
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio 2019/2022 (with the C++ and .NET desktop workloads)."
}

# Ask vswhere for the newest install that has MSBuild, and locate MSBuild.exe in it.
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild `
                      -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild not found. In the Visual Studio Installer add the C++ and .NET desktop workloads."
}

Write-Host "MSBuild  : $msbuild"
Write-Host "Solution : $sln"
Write-Host "Config   : $Configuration | Platform: x64`n"

# x64: the DirectShow driver is x64-only, and this also forces the C# exe to 64-bit.
& $msbuild $sln /t:Build /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal /nologo
if ($LASTEXITCODE -ne 0) {
    throw "Build failed (exit code $LASTEXITCODE)."
}

$out = Join-Path $root $Configuration
Write-Host "`nBuild succeeded. Output: $out"
