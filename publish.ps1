# Build and publish BarControl as a standalone, zero-dependency Windows executable
$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Publishing BarControl (Single-File Release)   " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# Stop any running BarControl instances so binaries aren't locked
Get-Process -Name "BarControl" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

$distDir = Join-Path $PSScriptRoot "dist"
if (Test-Path $distDir) {
    Remove-Item -Recurse -Force $distDir
}

dotnet publish BarControl.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $distDir

Write-Host ""
Write-Host "Publish complete! Executable located at:" -ForegroundColor Green
Write-Host (Join-Path $distDir "BarControl.exe") -ForegroundColor Yellow
Write-Host ""
Write-Host "You can move BarControl.exe anywhere or create a shortcut in your Startup folder." -ForegroundColor Green
