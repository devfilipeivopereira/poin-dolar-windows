param(
    [string]$Configuration = "Release",
    [string]$SetupName = "PoinDolarWindowsSetup.exe",
    [switch]$NoBuild,
    [switch]$OpenOnly,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$processNamesToKill = @("RtdDolarNative", "PoinDolarWindows")

function Stop-AppProcesses {
    param([int]$GraceSeconds = 5)

    $candidates = @()
    try {
        $candidates += Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $name = $_.Name
            $processNamesToKill -contains $name -or $name -like "*RtdDolar*"
        }
    }
    catch { }

    try {
        $candidates += Get-CimInstance Win32_Process | Where-Object {
            $_.Name -like "*RtdDolarNative*.exe" -or $_.Name -like "*PoinDolar*"
        }
    }
    catch { }

    if ($candidates.Count -gt 0) {
        $ids = $candidates | Select-Object -ExpandProperty ProcessId -Unique
        foreach ($id in $ids) {
            try {
                Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
            }
            catch { }
        }

        try {
            $ids | ForEach-Object {
                $id = $_
                $proc = Get-Process -Id $id -ErrorAction SilentlyContinue
                if ($proc) {
                    $proc.WaitForExit($GraceSeconds * 1000)
                }
            }
        }
        catch { }
    }
}

function Wait-ForUnlockedExecutable {
    param(
        [string]$Path,
        [int]$Attempts = 12,
        [int]$DelayMs = 500
    )

    if (-not (Test-Path $Path)) {
        return
    }

    for ($i = 0; $i -lt $Attempts; $i++) {
        try {
            $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $stream.Dispose()
            return
        }
        catch [System.IO.IOException] {
            if ($i -eq ($Attempts - 1)) {
                throw "Arquivo ainda em uso: $Path. Feche todos os processos do app e tente novamente."
            }

            Stop-AppProcesses
            Start-Sleep -Milliseconds $DelayMs
            continue
        }
    }
}

$root = Resolve-Path (Split-Path -Parent $PSCommandPath)
$projectRoot = Split-Path -Path $root -Parent
$installerScript = Join-Path $projectRoot "installer\build-installer.ps1"
$distSetup = Join-Path $projectRoot ("dist\" + $SetupName)
$setupLog = Join-Path $projectRoot "dist\installer.log"
$installDir = Join-Path $env:LOCALAPPDATA "PoinDolarWindows"
$appExe = @(
    [IO.Path]::Combine($installDir, "app", "x64", "RtdDolarNative.exe"),
    [IO.Path]::Combine($installDir, "app", "x86", "RtdDolarNative.exe"),
    [IO.Path]::Combine($installDir, "RtdDolarNative.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($null -eq $appExe) {
    $appExe = Join-Path $installDir "RtdDolarNative.exe"
}

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

    Stop-AppProcesses
    Wait-ForUnlockedExecutable -Path $appExe

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
