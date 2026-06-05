@echo off
setlocal
cd /d "%~dp0"

set "APP=.\artifacts\publish\Cameo-IFV\Cameo-IFV.exe"
if not exist "%APP%" set "APP=.\artifacts\publish\Cameo-IFV-clean-test\Cameo-IFV.exe"
if not exist "%APP%" (
  echo Published app not found: %APP%
  echo Build it first with the release publish command or GitHub Actions workflow.
  exit /b 1
)

"%APP%" %*
