$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$app = Join-Path $PSScriptRoot "artifacts\publish\Cameo-IFV\Cameo-IFV.exe"
if (-not (Test-Path -LiteralPath $app)) {
    $app = Join-Path $PSScriptRoot "artifacts\publish\Cameo-IFV-clean-test\Cameo-IFV.exe"
}

if (-not (Test-Path -LiteralPath $app)) {
    Write-Error "Published app not found: $app. Build it first with the release publish command or GitHub Actions workflow."
}

& $app @args
