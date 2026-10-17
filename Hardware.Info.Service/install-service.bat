@echo off
setlocal

rem Cai Hardware.JDX Service chay ngam (tu dong khoi dong cung Windows, chay bang LocalSystem).
rem Chay file nay bang quyen Administrator, dat cung thu muc voi Hardware.Info.Service.exe.

set "SVC=Hardware.JDX.Service"
set "EXE=%~dp0Hardware.Info.Service.exe"

net session >nul 2>nul
if errorlevel 1 (
    echo [LOI] Hay chay file nay bang quyen Administrator ^(chuot phai ^> Run as administrator^).
    goto :fail
)

if not exist "%EXE%" (
    echo [LOI] Khong thay "%EXE%".
    goto :fail
)

sc query "%SVC%" >nul 2>nul
if not errorlevel 1 (
    echo Service da ton tai, dung va go ban cu...
    sc stop "%SVC%" >nul 2>nul
    timeout /t 3 /nobreak >nul
    sc delete "%SVC%" >nul
    timeout /t 2 /nobreak >nul
)

sc create "%SVC%" binPath= "\"%EXE%\"" start= auto DisplayName= "Hardware.JDX Service"
if errorlevel 1 goto :fail
sc description "%SVC%" "Doc thong tin linh kien may tinh va gui len Kafka dinh ky." >nul
rem Tu khoi dong lai neu service bi loi: sau 60 giay, reset bo dem loi sau 1 ngay
sc failure "%SVC%" reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul

sc start "%SVC%"
if errorlevel 1 (
    echo [LOI] Khong khoi dong duoc service. Xem Event Viewer ^> Windows Logs ^> Application.
    goto :fail
)

echo.
echo === DA CAI VA CHAY %SVC% ===
echo Cau hinh: appsettings.json canh file exe ^(sua xong khong can cai lai; service nap lai moi chu ky^).
echo Log: Event Viewer ^> Windows Logs ^> Application ^(nguon Hardware.JDX.Service^).
endlocal
pause
exit /b 0

:fail
endlocal
pause
exit /b 1
