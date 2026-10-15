# TaskMonitor 빌드 스크립트
#   .\build.ps1            → Release 빌드 (bin\Release\net10.0-windows\TaskMonitor.exe)
#   .\build.ps1 -Publish   → publish\TaskMonitor\ 에 실행 파일 묶음 생성 (프레임워크 의존, 단일 폴더)
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
dotnet build TaskMonitor.slnx -c Release
if ($Publish) {
    dotnet publish src\TaskMonitor\TaskMonitor.csproj -c Release -o publish\TaskMonitor --self-contained false
    Write-Host "publish\TaskMonitor\TaskMonitor.exe"
} else {
    Write-Host "src\TaskMonitor\bin\Release\net10.0-windows\TaskMonitor.exe"
}
