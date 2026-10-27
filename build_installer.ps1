$ErrorActionPreference = "Stop"
$ROOT = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Auto-detect MSBuild via vswhere; fall back to common fixed path.
$VSWHERE = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
$MSBUILD = ""
if (Test-Path $VSWHERE) {
    $c = & $VSWHERE -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2>$null
    if ($c) { $MSBUILD = $c[0] }
}
if (-not $MSBUILD -or -not (Test-Path $MSBUILD)) {
    $MSBUILD = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
}
if (-not (Test-Path $MSBUILD)) { throw "MSBuild not found. Please install Visual Studio or Build Tools with the MSBuild component." }
Write-Host ("Using MSBuild: " + $MSBUILD)

# The installer project's PostBuildEvent (build_payload.ps1) builds the main app,
# zips its runtime files, and appends them to the output exe. So a single build is enough.
& $MSBUILD (Join-Path $ROOT "AdbToolsInstaller\AdbToolsInstaller.csproj") /p:Configuration=Release /p:Platform=AnyCPU /t:Build /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

Write-Host ""
Write-Host ("Build succeeded: " + (Join-Path $ROOT "AdbToolsInstaller\bin\Release\AdbToolsInstaller.exe"))
