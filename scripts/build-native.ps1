param([string]$Dotnet = 'dotnet', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$ramRoot = Split-Path -Parent $PSScriptRoot
$ramDotnet = $Dotnet
if ($Dotnet -eq 'dotnet' -and (Test-Path -LiteralPath (Join-Path $ramRoot '.tools\dotnet\dotnet.exe'))) { $ramDotnet = Join-Path $ramRoot '.tools\dotnet\dotnet.exe' }
$env:DOTNET_CLI_HOME = Join-Path $ramRoot '.test-data\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Push-Location $ramRoot
try {
    if (-not $SkipTests) {
        & $ramDotnet run --project native/RamFlow.Tests/RamFlow.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Native regression failed' }
        & $ramDotnet run --project native/RamFlow.Storage.Tests/RamFlow.Storage.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Storage regression failed' }
    }
    & $ramDotnet publish native/RamFlow.UI/RamFlow.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist/RamFlow-native
    if ($LASTEXITCODE -ne 0) { throw 'Native UI publish failed' }
    & $ramDotnet publish native/RamFlow.Service/RamFlow.Service.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist/RamFlow-native
    if ($LASTEXITCODE -ne 0) { throw 'Native service publish failed' }
    Copy-Item -LiteralPath native/Install.ps1,native/Uninstall.ps1,README.md -Destination dist/RamFlow-native -Force
    New-Item -ItemType Directory -Path dist/RamFlow-native/licenses -Force | Out-Null
    $ramSdkRoot = Split-Path -Parent (Get-Command $ramDotnet).Source
    foreach ($ramNotice in @('LICENSE.txt','ThirdPartyNotices.txt')) { if (Test-Path -LiteralPath (Join-Path $ramSdkRoot $ramNotice)) { Copy-Item -LiteralPath (Join-Path $ramSdkRoot $ramNotice) -Destination dist/RamFlow-native/licenses -Force } }
    python scripts/verify-native.py
    if ($LASTEXITCODE -ne 0) { throw 'Native packaged validation failed' }
    Compress-Archive -LiteralPath dist/RamFlow-native -DestinationPath dist/RamFlow-0.3.2-windows-x64.zip -Force
    $ramHash = (Get-FileHash -LiteralPath dist/RamFlow-0.3.2-windows-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath dist/SHA256SUMS-native.txt -Value "$ramHash  RamFlow-0.3.2-windows-x64.zip" -Encoding ascii
} finally { Pop-Location }
