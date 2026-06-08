$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $root 'src/RtdDolarNative/Rtd/RtdCompleteFieldCatalog.cs'
$configPath = Join-Path $root 'src/RtdDolarNative/Config/AppConfig.cs'
$snapshotPath = Join-Path $root 'src/RtdDolarNative/MarketData/MarketSnapshot.cs'
$statePath = Join-Path $root 'src/RtdDolarNative/MarketData/MarketState.cs'
$mainWindowPath = Join-Path $root 'src/RtdDolarNative/MainWindow.xaml.cs'
$xamlPath = Join-Path $root 'src/RtdDolarNative/MainWindow.xaml'
$csprojPath = Join-Path $root 'src/RtdDolarNative/RtdDolarNative.csproj'

if (-not (Test-Path $catalogPath)) {
    throw 'RtdCompleteFieldCatalog.cs was not found.'
}

$catalog = Get-Content -Raw $catalogPath
$fieldCount = ([regex]::Matches($catalog, 'Field\("')).Count
if ($fieldCount -ne 145) {
    throw "Expected 145 RTD complete fields, found $fieldCount."
}

$requiredGroups = @(
    'Mercado',
    'Contrato',
    'Performance',
    'Opcoes',
    'Volatilidade',
    'Tecnicos',
    'Fluxo/Agressao',
    'VWAP/Medias',
    'Diagnostico'
)

foreach ($group in $requiredGroups) {
    if ($catalog -notmatch [regex]::Escape('"' + $group + '"')) {
        throw "Missing RTD complete group: $group."
    }
}

$requiredCodes = @('ULT', 'VOL', 'OCP', 'OVD', '11', '1', '6', '7', '101_')
foreach ($code in $requiredCodes) {
    if ($catalog -notmatch [regex]::Escape('"' + $code + '"')) {
        throw "Missing RTD complete code: $code."
    }
}

$technicalCodes = @('11', '1', '6', '7')
foreach ($code in $technicalCodes) {
    $pattern = 'Field\("' + [regex]::Escape($code) + '".*GroupTechnical'
    if ($catalog -notmatch $pattern) {
        throw "Technical group must include RTD code: $code."
    }
}

if ($catalog -notmatch '"Campo 101_"') {
    throw 'Field 101_ must be labeled Campo 101_.'
}

$config = Get-Content -Raw $configPath
if ($config -notmatch 'RtdComplete') {
    throw 'RtdConfig does not expose RtdComplete session source support.'
}

$defaultFieldsMatch = [regex]::Match($config, 'DefaultQuoteFields\s*=\s*new\[\]\s*\{(?<body>.*?)\};', 'Singleline')
if (-not $defaultFieldsMatch.Success) {
    throw 'Could not inspect DefaultQuoteFields.'
}

$defaultQuoteFieldCount = ([regex]::Matches($defaultFieldsMatch.Groups['body'].Value, '"[^"]+"')).Count
if ($defaultQuoteFieldCount -gt 40) {
    throw "DefaultQuoteFields must stay lightweight; found $defaultQuoteFieldCount fields."
}

$snapshot = Get-Content -Raw $snapshotPath
if ($snapshot -notmatch 'FieldUpdatedAt') {
    throw 'MarketSnapshot must expose FieldUpdatedAt timestamps.'
}

$state = Get-Content -Raw $statePath
if ($state -notmatch 'FieldUpdatedAt\[') {
    throw 'MarketState.Update must update FieldUpdatedAt per normalized field.'
}

$mainWindow = Get-Content -Raw $mainWindowPath
if ($mainWindow -notmatch 'TabRtdComplete') {
    throw 'MainWindow must define TabRtdComplete.'
}

if ($mainWindow -notmatch 'RenderRtdComplete') {
    throw 'MainWindow must render RTD Completo.'
}

if ($mainWindow -notmatch 'ApplyRtdCompleteSubscriptions') {
    throw 'MainWindow must apply RTD complete subscriptions on demand.'
}

$xaml = Get-Content -Raw $xamlPath
if ($xaml -notmatch 'RTD Completo') {
    throw 'MainWindow.xaml must include the RTD Completo tab and navigation text.'
}

if ($xaml -notmatch 'RtdCompleteGrid') {
    throw 'MainWindow.xaml must include the RTD complete grid.'
}

$csproj = Get-Content -Raw $csprojPath
if ($csproj -notmatch 'RtdCompleteFieldCatalog.cs') {
    throw 'Project file must compile RtdCompleteFieldCatalog.cs.'
}

Write-Host 'RTD complete static verification passed.'
