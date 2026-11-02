param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.dotnet/dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
if (-not $SkipTests) {
    & $dotnetExe run --project (Join-Path $projectRoot 'tests/SelectionAssistant.Tests') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
}
& $dotnetExe publish (Join-Path $projectRoot 'src/SelectionAssistant.Windows') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o (Join-Path $projectRoot 'artifacts/win-x64')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $projectRoot 'artifacts/win-x64/使用说明.md') -Force
Write-Host 'Ready: artifacts/win-x64/SelectionAssistant.exe'
