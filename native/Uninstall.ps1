param([string]$TargetDirectory = $PSScriptRoot, [switch]$NoIntegration)
$ErrorActionPreference = 'Stop'
$ramTarget = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\')
$ramParent = Split-Path -Parent $ramTarget
if (-not $ramParent -or $ramTarget -eq [IO.Path]::GetPathRoot($ramTarget).TrimEnd('\')) { throw '제거 경로 검증 실패' }
if ((Get-Item -LiteralPath $ramTarget).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '연결된 경로를 제거하지 않습니다.' }
$ramMarker = Join-Path $ramTarget 'RamFlow-install.json'
if (-not (Test-Path -LiteralPath $ramMarker) -or (Get-Content -LiteralPath $ramMarker -Raw | ConvertFrom-Json).product -ne 'homesweetlove.RamFlow') { throw '설치 기록이 없어 자동 제거하지 않습니다.' }
foreach ($ramProcess in Get-Process -Name RamFlow -ErrorAction SilentlyContinue) { if ($ramProcess.Path -eq (Join-Path $ramTarget 'RamFlow.exe')) { throw '트레이에서 RamFlow를 종료한 뒤 제거하세요.' } }
foreach ($ramItem in Get-ChildItem -LiteralPath $ramTarget -Recurse -Force) { if ($ramItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '설치 폴더의 연결 파일을 먼저 제거하세요.' } }
$ramHelper = Join-Path $ramTarget 'RamFlow.Service.exe'
if (-not $NoIntegration) {
    & $ramHelper --restore-and-stop
    if ($LASTEXITCODE -ne 0) { throw '자원 복원 실패 · 설치를 유지합니다.' }
    $ramPagefileBackup = Join-Path $env:LOCALAPPDATA 'RamFlowNative\pagefile-original.json'
    if (Test-Path -LiteralPath $ramPagefileBackup) { & $ramHelper --restore-pagefile $ramPagefileBackup; if ($LASTEXITCODE -ne 0) { throw '관리자 권한으로 Pagefile을 먼저 복원하세요. 설치를 유지합니다.' } }
    $ramService = Get-CimInstance Win32_Service -Filter "Name='RamFlowMonitor'" -ErrorAction SilentlyContinue
    $ramAdminTarget = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'RamFlowMonitor'))
    $ramAdminHelper = Join-Path $ramAdminTarget 'RamFlow.Service.exe'
    if ($ramService -and $ramService.PathName -eq ('"' + $ramAdminHelper + '" --service')) {
        Stop-Service RamFlowMonitor; & sc.exe delete RamFlowMonitor; if ($LASTEXITCODE -ne 0) { throw '서비스 제거 실패' }
        if ($ramAdminTarget.StartsWith([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and (Get-Content -LiteralPath (Join-Path $ramAdminTarget 'RamFlow-install.json') -Raw | ConvertFrom-Json).product -eq 'homesweetlove.RamFlow') {
            Remove-Item -LiteralPath $ramAdminTarget -Recurse -Force
        }
        $ramMonitorState = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'RamFlowMonitor'))
        $ramStateMarker = Join-Path $ramMonitorState 'RamFlow-monitor-state.json'
        if ($ramMonitorState.StartsWith([IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $ramStateMarker) -and (Get-Content -LiteralPath $ramStateMarker -Raw | ConvertFrom-Json).product -eq 'homesweetlove.RamFlow') { Remove-Item -LiteralPath $ramMonitorState -Recurse -Force }
    }
    $ramRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $ramValue = (Get-ItemProperty -Path $ramRun -Name RamFlow -ErrorAction SilentlyContinue).RamFlow
    if ($ramValue -eq ('"' + (Join-Path $ramTarget 'RamFlow.exe') + '"')) { Remove-ItemProperty -Path $ramRun -Name RamFlow }
    $ramMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'RamFlow.lnk'
    if (Test-Path -LiteralPath $ramMenu) { $ramShortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($ramMenu); if ($ramShortcut.TargetPath -eq (Join-Path $ramTarget 'RamFlow.exe')) { Remove-Item -LiteralPath $ramMenu } }
}
# 설치 폴더만 제거한다. 모델 파일·클라우드 파일·복구 메타데이터는 보존한다.
if (-not $ramTarget.StartsWith($ramParent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '최종 제거 경로 검증 실패' }
Remove-Item -LiteralPath $ramTarget -Recurse -Force
Write-Output '제거 완료 · 사용자 모델과 복구 메타데이터는 유지했습니다.'
