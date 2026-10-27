param(
    [string]$Configuration = "Release",
    [string]$ProjectDir
)
$ErrorActionPreference = "Stop"
if (-not $ProjectDir) { $ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$ROOT = Split-Path -Parent $ProjectDir.TrimEnd('\')

# Prefer VS2022 MSBuild (handles both classic and SDK-style projects).
$MSBUILD = ""
$cands = @(
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
)
foreach ($c in $cands) { if (Test-Path $c) { $MSBUILD = $c; break } }
if (-not $MSBUILD) {
    $VSWHERE = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $VSWHERE) {
        $c = & $VSWHERE -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2>$null
        if ($c) { $MSBUILD = $c[0] }
    }
}
if (-not $MSBUILD -or -not (Test-Path $MSBUILD)) { throw "MSBuild not found. Please install Visual Studio or Build Tools with the MSBuild component." }
Write-Host ("Using MSBuild: " + $MSBUILD)

# 1) Build the main app (AdbTools, Release/AnyCPU -> x64 binaries). The Update dependency is built alongside it.
& $MSBUILD (Join-Path $ROOT "AdbTools\AdbTools.csproj") /p:Configuration=Release /p:Platform=AnyCPU /t:Build /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Main app build failed." }

# 1.1) Build the standalone uninstaller (small, no payload) -> becomes uninstall.exe in the install dir
$UNINST_PROJ = Join-Path $ROOT "AdbToolsUninstaller\AdbToolsUninstaller.csproj"
# Restore 用 quiet 级别：避免打印本地化中文（中文消息在管道中被按 GBK 解码会乱码）
& $MSBUILD $UNINST_PROJ /t:Restore /nologo /v:quiet
& $MSBUILD $UNINST_PROJ /p:Configuration=Release /t:Build /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Uninstaller build failed." }
$UNINST_EXE = Join-Path $ROOT "AdbToolsUninstaller\bin\Release\AdbToolsUninstaller.exe"
if (-not (Test-Path $UNINST_EXE)) { throw ("Uninstaller output not found: " + $UNINST_EXE) }

# 2) Copy every *.exe / *.dll from the main app output dir into payload_stage.
$SRC = Join-Path $ROOT "AdbTools\bin\Release"
if (-not (Test-Path $SRC)) { throw ("Main app output not found: " + $SRC + " (build AdbTools first).") }
$STAGE = Join-Path $ProjectDir "payload_stage"
if (Test-Path $STAGE) { Remove-Item $STAGE -Recurse -Force }
New-Item -ItemType Directory -Path $STAGE | Out-Null

$patterns = @("*.exe", "*.dll")
$count = 0
foreach ($p in $patterns) {
    Get-ChildItem $SRC -Filter $p -File | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $STAGE $_.Name)
        $count++
    }
}
if ($count -eq 0) { throw ("No *.exe / *.dll found in " + $SRC + "; nothing to package.") }
Write-Host ("Staged " + $count + " files -> " + $STAGE)

# 2.1) 把独立卸载器作为 uninstall.exe 打包（安装时释放到安装目录，避免复制整个安装器）
Copy-Item $UNINST_EXE (Join-Path $STAGE "uninstall.exe") -Force
Write-Host ("Staged uninstaller -> uninstall.exe (" + [math]::Round((Get-Item $UNINST_EXE).Length / 1KB, 1) + " KB)")

# 3) Compress payload_stage into payload.zip (compiled into the installer as an embedded resource).
$PAYLOAD = Join-Path $ProjectDir "payload.zip"
if (Test-Path $PAYLOAD) { Remove-Item $PAYLOAD -Force }
Compress-Archive -Path (Join-Path $STAGE "*") -DestinationPath $PAYLOAD -Force
Write-Host ("Created payload.zip -> " + $PAYLOAD + " (" + ([math]::Round((Get-Item $PAYLOAD).Length / 1MB, 2)) + " MB)")
