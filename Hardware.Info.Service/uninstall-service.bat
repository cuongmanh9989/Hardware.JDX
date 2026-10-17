@echo off
setlocal

rem Dung va go Hardware.JDX Service. Chay bang quyen Administrator.

set "SVC=Hardware.JDX.Service"

net session >nul 2>nul
if errorlevel 1 (
    echo [LOI] Hay chay file nay bang quyen Administrator ^(chuot phai ^> Run as administrator^).
    goto :fail
)

sc query "%SVC%" >nul 2>nul
if errorlevel 1 (
    echo Service %SVC% chua duoc cai.
    goto :done
)

sc stop "%SVC%" >nul 2>nul
timeout /t 3 /nobreak >nul
sc delete "%SVC%"
if errorlevel 1 goto :fail

:done
echo === XONG ===
endlocal
pause
exit /b 0

:fail
endlocal
pause
exit /b 1
