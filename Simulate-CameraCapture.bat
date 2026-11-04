@echo off
title AURA - Mo phong thiet bi Camera y te tu dong
color 0b

echo ======================================================================
echo    HE THONG AURA - TRINH MO PHONG THIET BI CHUP DAY MAT (CAMERA IOT)
echo ======================================================================
echo.

set "IMAGE=%~1"
if "%IMAGE%"=="" (
    set /p "IMAGE=Nhap ten file anh can chup (vi du: Image_01L.jpg hoac an Enter de dung mac dinh): "
)
if "%IMAGE%"=="" set "IMAGE=Image_01L.jpg"

echo.
echo [*] Thiet bi dang chup va tu dong gui goi tin vao he thong AURA...
echo.

set "SCRIPT_PATH=%~dp0scripts\dev\Send-WebImageToNiFi.ps1"
if not exist "%SCRIPT_PATH%" set "SCRIPT_PATH=%~dp0Send-WebImageToNiFi.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PATH%" -Image "%IMAGE%"

echo.
echo ======================================================================
echo Giao dich hoan tat! Ban co the mo Apache NiFi de xem Data Provenance.
echo ======================================================================
echo.
pause
