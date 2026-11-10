param([string]$Dotnet = 'dotnet', [switch]$SkipTests, [string]$ArchiveName = 'RamFlow-0.4.0-carp.2-research-windows-x64.zip')
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
        python scripts/test_benchmark_observer.py
        if ($LASTEXITCODE -ne 0) { throw 'Standalone benchmark observer regression failed' }
        python scripts/test_research_matrix.py
        if ($LASTEXITCODE -ne 0) { throw 'Research matrix regression failed' }
        python scripts/test_research_analyze.py
        if ($LASTEXITCODE -ne 0) { throw 'Research analysis regression failed' }
        python scripts/test_research_logcheck.py
        if ($LASTEXITCODE -ne 0) { throw 'Research evidence-integrity regression failed' }
        python scripts/test_research_lab_adapter.py
        if ($LASTEXITCODE -ne 0) { throw 'Research lab binding regression failed' }
        & $ramDotnet run --project native/RamFlow.Tests/RamFlow.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Native regression failed' }
        & $ramDotnet run --project native/RamFlow.Storage.Tests/RamFlow.Storage.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Storage regression failed' }
    }
    & $ramDotnet publish native/RamFlow.UI/RamFlow.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist/RamFlow-native
    if ($LASTEXITCODE -ne 0) { throw 'Native UI publish failed' }
    & $ramDotnet publish native/RamFlow.Service/RamFlow.Service.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist/RamFlow-native
    if ($LASTEXITCODE -ne 0) { throw 'Native service publish failed' }
    & $ramDotnet publish native/RamFlow.Tray/RamFlow.Tray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -nodeReuse:false -o dist/RamFlow-native
    if ($LASTEXITCODE -ne 0) { throw 'Native tray publish failed' }
    Copy-Item -LiteralPath native/Install.ps1,native/Uninstall.ps1,README.md,README.ko.md -Destination dist/RamFlow-native -Force
    Copy-Item -LiteralPath scripts/benchmark-observer.py,docs/BENCHMARK.md -Destination dist/RamFlow-native -Force
    New-Item -ItemType Directory -Path dist/RamFlow-native/licenses -Force | Out-Null
    $ramSdkRoot = Split-Path -Parent (Get-Command $ramDotnet).Source
    foreach ($ramNotice in @('LICENSE.txt','ThirdPartyNotices.txt')) { if (Test-Path -LiteralPath (Join-Path $ramSdkRoot $ramNotice)) { Copy-Item -LiteralPath (Join-Path $ramSdkRoot $ramNotice) -Destination dist/RamFlow-native/licenses -Force } }
    python scripts/verify-native.py
    if ($LASTEXITCODE -ne 0) { throw 'Native packaged validation failed' }
    if ([IO.Path]::GetFileName($ArchiveName) -ne $ArchiveName -or -not $ArchiveName.EndsWith('.zip')) { throw 'ArchiveName must be a ZIP filename' }
    $ramArchive = Join-Path (Join-Path $ramRoot 'dist') $ArchiveName
    Compress-Archive -LiteralPath dist/RamFlow-native -DestinationPath $ramArchive -Force
    $ramHash = (Get-FileHash -LiteralPath $ramArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $ramSumName = if ($ArchiveName -eq 'RamFlow-0.3.4-windows-x64.zip') { 'SHA256SUMS-native.txt' } else { $ArchiveName + '.sha256.txt' }
    Set-Content -LiteralPath (Join-Path (Join-Path $ramRoot 'dist') $ramSumName) -Value "$ramHash  $ArchiveName" -Encoding ascii
} finally { Pop-Location }
