@echo off
setlocal

rem Build Hardware.Info.Wpf thanh 1 file exe portable (self-contained, khong can cai .NET).
rem Cach dung:  build-portable.bat [runtime]
rem   runtime: win-x64 (mac dinh) | win-arm64 | win-x86

set "RID=%~1"
if "%RID%"=="" set "RID=win-x64"

set "ROOT=%~dp0"
set "PROJECT=%ROOT%Hardware.Info.Wpf\Hardware.Info.Wpf.csproj"
set "OUTROOT=%ROOT%..\Hardware.JDX-portable"
set "OUTDIR=%OUTROOT%\%RID%"
set "ZIPFILE=%OUTROOT%\Hardware.JDX-portable-%RID%.zip"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [LOI] Khong tim thay dotnet. Hay cai .NET SDK 8 tro len.
    goto :fail
)

echo === Build portable cho %RID% ===
if exist "%OUTDIR%" rmdir /s /q "%OUTDIR%"

dotnet publish "%PROJECT%" -c Release -r %RID% --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=None ^
    -p:GenerateDocumentationFile=false ^
    -p:NoWarn=CS1591 ^
    -nodeReuse:false ^
    -o "%OUTDIR%"
if errorlevel 1 (
    echo [LOI] dotnet publish that bai.
    goto :fail
)

del /q "%OUTDIR%\*.xml" 2>nul

echo === Nen file zip ===
powershell -NoProfile -Command "Compress-Archive -Path '%OUTDIR%\Hardware.Info.Wpf.exe' -DestinationPath '%ZIPFILE%' -Force"
if errorlevel 1 (
    echo [LOI] Nen zip that bai.
    goto :fail
)

echo.
echo === XONG ===
echo Exe: %OUTDIR%\Hardware.Info.Wpf.exe
echo Zip: %ZIPFILE%
endlocal
pause
exit /b 0

:fail
endlocal
pause
exit /b 1
