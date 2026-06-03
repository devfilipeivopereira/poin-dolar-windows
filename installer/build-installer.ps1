param(
    [string]$Configuration = "Release",
    [string]$SetupName = "PoinDolarWindowsSetup.exe"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$solution = Join-Path $root "RtdDolarNative.sln"
$projectDir = Join-Path $root "src\RtdDolarNative"
$dist = Join-Path $root "dist"
$payloadRoot = Join-Path $dist "payload"
$payloadZip = Join-Path $dist "payload.zip"
$setupPath = Join-Path $dist $SetupName
$iconPath = Join-Path $projectDir "Assets\AppIcon.ico"
$installerSource = Join-Path $PSScriptRoot "Installer.cs"

$msbuild64 = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
$msbuild32 = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
$csc64 = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (!(Test-Path $msbuild64)) { throw "MSBuild x64 nao encontrado em $msbuild64" }
if (!(Test-Path $msbuild32)) { throw "MSBuild x86 nao encontrado em $msbuild32" }
if (!(Test-Path $csc64)) { throw "csc x64 nao encontrado em $csc64" }
if (!(Test-Path $iconPath)) { throw "Icone nao encontrado em $iconPath" }

& $msbuild64 $solution /t:Build /p:Configuration=$Configuration /p:Platform=x64 /m /nologo
& $msbuild32 $solution /t:Build /p:Configuration=$Configuration /p:Platform=x86 /m /nologo

Remove-Item -Recurse -Force $payloadRoot -ErrorAction SilentlyContinue
Remove-Item -Force $payloadZip,$setupPath -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $payloadRoot "app\x64"),(Join-Path $payloadRoot "app\x86"),(Join-Path $payloadRoot "docs") | Out-Null

$x64Out = Join-Path $projectDir "bin\x64\$Configuration"
$x86Out = Join-Path $projectDir "bin\x86\$Configuration"
$files = @("RtdDolarNative.exe", "RtdDolarNative.exe.config", "appsettings.json")

foreach ($file in $files) {
    Copy-Item -Force (Join-Path $x64Out $file) (Join-Path $payloadRoot "app\x64")
    Copy-Item -Force (Join-Path $x86Out $file) (Join-Path $payloadRoot "app\x86")
}

Copy-Item -Force (Join-Path $root "README.md") $payloadRoot
Copy-Item -Recurse -Force (Join-Path $root "docs\*") (Join-Path $payloadRoot "docs")

Compress-Archive -Path (Join-Path $payloadRoot "*") -DestinationPath $payloadZip -Force

& $csc64 `
    /nologo `
    /target:winexe `
    /out:$setupPath `
    /win32icon:$iconPath `
    /resource:$payloadZip,PoinDolarPayload.zip `
    /reference:System.Windows.Forms.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:Microsoft.CSharp.dll `
    $installerSource

Write-Host "Instalador gerado em: $setupPath"
