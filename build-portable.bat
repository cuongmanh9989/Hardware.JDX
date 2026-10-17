@echo off
setlocal

rem Build portable (self-contained, khong can cai .NET):
rem   1) Hardware.Info.Wpf     : app WPF 1 file exe
rem   2) Hardware.Info.Service : service chay ngam gui Kafka dinh ky (kem script cai/go service)
rem Cach dung:  build-portable.bat [runtime]
rem   runtime: win-x64 (mac dinh) | win-arm64 | win-x86

set "RID=%~1"
if "%RID%"=="" set "RID=win-x64"

set "ROOT=%~dp0"
set "PROJECT=%ROOT%Hardware.Info.Wpf\Hardware.Info.Wpf.csproj"
set "SVC_PROJECT=%ROOT%Hardware.Info.Service\Hardware.Info.Service.csproj"
set "OUTROOT=%ROOT%..\Hardware.JDX-portable"
set "OUTDIR=%OUTROOT%\%RID%"
set "SVC_OUTDIR=%OUTROOT%\%RID%-service"
set "ZIPFILE=%OUTROOT%\Hardware.JDX-portable-%RID%.zip"
set "SVC_ZIPFILE=%OUTROOT%\Hardware.JDX-service-%RID%.zip"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [LOI] Khong tim thay dotnet. Hay cai .NET SDK 8 tro len.
    goto :fail
)

echo === [1/2] Build app WPF portable cho %RID% ===
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
    echo [LOI] dotnet publish app WPF that bai.
    goto :fail
)

del /q "%OUTDIR%\*.xml" 2>nul

echo === [2/2] Build service chay ngam cho %RID% ===
if exist "%SVC_OUTDIR%" rmdir /s /q "%SVC_OUTDIR%"

dotnet publish "%SVC_PROJECT%" -c Release -r %RID% --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=None ^
    -p:GenerateDocumentationFile=false ^
    -p:NoWarn=CS1591 ^
    -nodeReuse:false ^
    -o "%SVC_OUTDIR%"
if errorlevel 1 (
    echo [LOI] dotnet publish service that bai.
    goto :fail
)

del /q "%SVC_OUTDIR%\*.xml" 2>nul

echo === Nen file zip ===
rem appsettings.json (cau hinh Kafka) phai di kem exe, neu khong app/service khong gui duoc
powershell -NoProfile -Command "Compress-Archive -Path '%OUTDIR%\Hardware.Info.Wpf.exe','%OUTDIR%\appsettings.json' -DestinationPath '%ZIPFILE%' -Force"
if errorlevel 1 (
    echo [LOI] Nen zip app WPF that bai.
    goto :fail
)
powershell -NoProfile -Command "Compress-Archive -Path '%SVC_OUTDIR%\Hardware.Info.Service.exe','%SVC_OUTDIR%\appsettings.json','%SVC_OUTDIR%\install-service.bat','%SVC_OUTDIR%\uninstall-service.bat' -DestinationPath '%SVC_ZIPFILE%' -Force"
if errorlevel 1 (
    echo [LOI] Nen zip service that bai.
    goto :fail
)

echo.
echo === XONG ===
echo App WPF : %OUTDIR%\Hardware.Info.Wpf.exe
echo Zip WPF : %ZIPFILE%
echo Service : %SVC_OUTDIR%\Hardware.Info.Service.exe
echo Zip svc : %SVC_ZIPFILE%
echo.
echo Cai service tren may dich: giai nen zip service, chay install-service.bat bang quyen Administrator.
endlocal
pause
exit /b 0

:fail
endlocal
pause
exit /b 1
