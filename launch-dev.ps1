$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot
dotnet run --project ".\src\CameoIFV.App\CameoIFV.App.csproj" @args
