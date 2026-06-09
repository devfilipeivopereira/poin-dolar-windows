param(
    [string]$Configuration = "Release",
    [string]$SetupName = "PoinDolarWindowsSetup.exe",
    [switch]$NoBuild,
    [switch]$OpenOnly,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Split-Path -Parent $PSCommandPath)
$projectRoot = Split-Path -Path $root -Parent
$installerScript = Join-Path $projectRoot "installer\build-installer.ps1"
$distSetup = Join-Path $projectRoot ("dist\" + $SetupName)
$setupLog = Join-Path $projectRoot "dist\installer.log"
$installDir = Join-Path $env:LOCALAPPDATA "PoinDolarWindows"
$appExe = Join-Path $installDir "RtdDolarNative.exe"

if (-not $OpenOnly) {
    if (-not (Test-Path $installerScript)) {
        throw "Script de build do instalador nao encontrado: $installerScript"
    }

    if (-not $NoBuild) {
        Write-Host "Compilando instalador: $Configuration..."
        & powershell -ExecutionPolicy Bypass -File $installerScript -Configuration $Configuration 2>&1 | Tee-Object -FilePath $setupLog
    }

    if (-not (Test-Path $distSetup)) {
        throw "Instalador nao encontrado apos o build: $distSetup"
    }

    Get-Process -Name "RtdDolarNative" -ErrorAction SilentlyContinue | Stop-Process -Force
    $args = @("--launch")
    if ($Quiet) {
        $args = @("--quiet", "--launch")
    }

    Write-Host "Reinstalando em: $installDir"
    & $distSetup @args
}

if (Test-Path $appExe) {
    Write-Host "Abrindo $appExe"
    Start-Process $appExe
}
else {
    throw "Executavel nao encontrado em $appExe. Instalacao falhou ou app nao esta instalado."
}
