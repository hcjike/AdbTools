@echo off
setlocal
set ROOT=%~dp0
set MSBUILD="C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
set INS_DIR=%ROOT%AdbToolsInstaller
set STAGE=%INS_DIR%\payload_stage
set PAYLOAD=%INS_DIR%\payload.zip
set FINAL=%INS_DIR%\bin\Release\AdbToolsInstaller.exe

echo [1/5] 编译主程序 (Release x64)...
%MSBUILD% %ROOT%AdbTools.sln /p:Configuration=Release /p:Platform=x64 /t:Build /nologo /v:minimal
if errorlevel 1 goto :err

echo [2/5] 准备运行文件...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"
set SRC=%ROOT%AdbTools\bin\x64\Release
for %%f in (adb.exe AdbWinApi.dll AdbWinUsbApi.dll QRCoder.dll AdbTools.exe AdbTools.exe.config AdbTools.exe.manifest Update.exe Update.exe.config) do (
    if exist "%SRC%\%%f" copy /y "%SRC%\%%f" "%STAGE%\" >nul
)

echo [3/5] 生成载荷压缩包...
if exist "%PAYLOAD%" del /f /q "%PAYLOAD%"
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%PAYLOAD%' -Force"
if errorlevel 1 goto :err

echo [4/5] 编译安装程序 (Release)...
copy /y "%ROOT%AdbTools\icon.ico" "%INS_DIR%\appicon.ico" >nul
%MSBUILD% "%INS_DIR%\AdbToolsInstaller.csproj" /p:Configuration=Release /p:Platform=AnyCPU /t:Build /nologo /v:minimal
if errorlevel 1 goto :err

echo [5/5] 将载荷追加到安装程序，生成单文件 exe...
powershell -NoProfile -Command ^
  "$exe=[IO.File]::ReadAllBytes('%FINAL%'); $zip=[IO.File]::ReadAllBytes('%PAYLOAD%'); $len=[BitConverter]::GetBytes([long]$zip.Length); [IO.File]::WriteAllBytes('%FINAL%', ($exe + $zip + $len));"
if errorlevel 1 goto :err

echo.
echo 构建成功：%FINAL%
goto :eof

:err
echo.
echo 构建失败。
exit /b 1
