@echo off
setlocal
cd /d "%~dp0"
dotnet run --project ".\src\CameoIFV.App\CameoIFV.App.csproj" %*
