param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$ramRoot = Split-Path -Parent $PSScriptRoot
Push-Location $ramRoot
try {
    $env:RAMFLOW_HOME = Join-Path $ramRoot '.test-data'
    if (-not $SkipTests) {
        python -m pytest -q
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    python -m PyInstaller --noconfirm --onedir --windowed --name RamFlow --paths src --hidden-import win32timezone --hidden-import win32security --hidden-import win32pipe --hidden-import win32file --exclude-module tkinter --exclude-module matplotlib --exclude-module numpy scripts/desktop.py
    if ($LASTEXITCODE -ne 0) { throw 'GUI build failed' }
    python -m PyInstaller --noconfirm --onedir --console --name RamFlow-cli --paths src --hidden-import win32timezone --hidden-import win32security --hidden-import win32pipe --hidden-import win32file --exclude-module tkinter --exclude-module matplotlib --exclude-module numpy scripts/cli.py
    if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }
    Copy-Item -LiteralPath README.md -Destination dist/RamFlow/README.md -Force
    Compress-Archive -Path dist/RamFlow,dist/RamFlow-cli -DestinationPath dist/RamFlow-0.2.0-windows-x64.zip -Force
} finally {
    Pop-Location
}
