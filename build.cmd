@echo off
echo Building BarControl...
dotnet build BarControl.sln -c Release
if %ERRORLEVEL% equ 0 (
    echo.
    echo Build succeeded with 0 errors!
) else (
    echo.
    echo Build failed!
)
pause
