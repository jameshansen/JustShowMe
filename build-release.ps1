<#
.SYNOPSIS
    Produces a clean, distributable Release build of JustShowMe and zips it for a
    GitHub release.

.DESCRIPTION
    1. Reads the build number from build.number (starts at 0005).
    2. Writes it into justshowme_gui\BuildInfo.cs so the title bar shows "- Build NNNN".
    3. Builds the solution as Release|x64. Costura.Fody (Release only) embeds the
       managed dependencies into justshowme_gui.exe, so the output is clean.
    4. Stages ONLY the files needed to run, then zips them to
       justshowme_build<NNNN>.zip in the repo root.
    5. Increments build.number for next time.

    Requires Visual Studio 2019/2022 (C++ and .NET desktop workloads).
    Close any app using the virtual camera first, or the driver DLL link will fail.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build-release.ps1
#>
[CmdletBinding()]
param([switch]$KeepStaging)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# 1. Build number (zero-padded to 4 digits).
$counterFile = Join-Path $root 'build.number'
$n = 5
if (Test-Path $counterFile) {
    $raw = (Get-Content $counterFile -Raw).Trim()
    if ($raw -match '^\d+$') { $n = [int]$raw }
}
$build = '{0:D4}' -f $n
Write-Host "=== JustShowMe release build $build ===`n"

# 2. Stamp the build number into the GUI.
$buildInfo = Join-Path $root 'justshowme_gui\BuildInfo.cs'
@"
namespace JustShowMe
{
    /// Release build number. Bumped by build-release.ps1 on each GitHub release;
    /// shown in the window title bar.
    internal static class BuildInfo
    {
        public const string Number = "$build";
    }
}
"@ | Set-Content -Encoding UTF8 $buildInfo

# 3. Locate MSBuild and build Release|x64.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found. Install Visual Studio 2019/2022." }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found." }

& $msbuild (Join-Path $root 'JustShowMe.sln') /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE). If LNK1168, close apps using the virtual camera." }

# 4. Stage only the files a user needs to run.
$relDir = Join-Path $root 'Release'
$stageRoot = Join-Path $root 'release-staging'
$stage = Join-Path $stageRoot "justshowme_build$build"
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# Everything needed to run, all flat beside the exe. Costura embeds the managed
# deps into the exe; the native OpenCvSharp DLLs can't be embedded so they ship here.
$files = @(
    'justshowme_gui.exe',
    'justshowme_cam.dll',
    'justshowme_filter.dll',
    'face_detection_yunet_2023mar.onnx',
    'face_recognition_sface_2021dec.onnx',
    'OpenCvSharpExtern.dll',
    'opencv_videoio_ffmpeg4110_64.dll'
)
foreach ($f in $files) {
    $src = Join-Path $relDir $f
    if (-not (Test-Path $src)) { throw "Expected release file missing: $f" }
    Copy-Item $src (Join-Path $stage $f)
}
# License for the GitHub release.
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE') }

# 5. Zip it (contents under a justshowme_build<NNNN>\ folder).
$zip = Join-Path $root "justshowme_build$build.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip

if (-not $KeepStaging) { Remove-Item $stageRoot -Recurse -Force }

# 6. Increment the counter for next time.
('{0:D4}' -f ($n + 1)) | Set-Content -Encoding ASCII $counterFile

Write-Host "`nRelease $build done."
Write-Host "  Zip: $zip"
Write-Host "  Next build will be $('{0:D4}' -f ($n + 1))."
