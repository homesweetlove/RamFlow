param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$TargetDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\RamFlow'),
    [switch]$Startup,
    [switch]$DesktopShortcut,
    [switch]$MonitorService,
    [switch]$NoIntegration,
    [switch]$Launch,
    [int]$WaitPid = 0
)
$ErrorActionPreference = 'Stop'
$ramSource = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
$ramTarget = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\')
$ramParent = Split-Path -Parent $ramTarget
if (-not $ramParent -or $ramTarget -eq [IO.Path]::GetPathRoot($ramTarget).TrimEnd('\')) { throw '설치 대상으로 루트 디렉터리를 사용할 수 없습니다.' }
foreach ($ramAncestor in @($ramTarget, $ramParent)) { if ((Test-Path -LiteralPath $ramAncestor) -and ((Get-Item -LiteralPath $ramAncestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '연결된 경로에는 설치하지 않습니다.' } }
if (-not (Test-Path -LiteralPath (Join-Path $ramSource 'RamFlow.exe')) -or -not (Test-Path -LiteralPath (Join-Path $ramSource 'RamFlow.Service.exe'))) { throw '완전한 RamFlow 배포 폴더를 선택하세요.' }
if ($WaitPid -gt 0) { Wait-Process -Id $WaitPid -Timeout 30 -ErrorAction SilentlyContinue; if (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) { throw 'RamFlow 종료를 기다린 뒤 다시 실행하세요.' } }
foreach ($ramEngineProcess in Get-Process -Name 'RamFlow.Service' -ErrorAction SilentlyContinue) { if ($ramEngineProcess.Path -eq (Join-Path $ramTarget 'RamFlow.Service.exe')) { Wait-Process -Id $ramEngineProcess.Id -Timeout 5 -ErrorAction SilentlyContinue; if (Get-Process -Id $ramEngineProcess.Id -ErrorAction SilentlyContinue) { throw '설치 폴더의 엔진을 종료한 뒤 다시 실행하세요.' } } }
$ramMarker = Join-Path $ramTarget 'RamFlow-install.json'
if ((Test-Path -LiteralPath $ramTarget) -and $ramTarget -ne $ramSource -and -not (Test-Path -LiteralPath $ramMarker)) { throw '다른 파일이 있는 폴더를 덮어쓰지 않습니다.' }
if (Test-Path -LiteralPath $ramMarker) { if ((Get-Content -LiteralPath $ramMarker -Raw | ConvertFrom-Json).product -ne 'homesweetlove.RamFlow') { throw '설치 기록이 다릅니다.' } }
New-Item -ItemType Directory -Path $ramParent -Force | Out-Null
$ramStage = [IO.Path]::GetFullPath($ramTarget + '.stage-' + [guid]::NewGuid().ToString('N'))
$ramBackup = [IO.Path]::GetFullPath($ramTarget + '.previous-' + [guid]::NewGuid().ToString('N'))
foreach ($ramManagedPath in @($ramStage, $ramBackup)) { if (-not $ramManagedPath.StartsWith($ramParent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '설치 경로 검증 실패' } }
if ($ramTarget -ne $ramSource) {
    New-Item -ItemType Directory -Path $ramStage | Out-Null
    try {
        foreach ($ramFile in Get-ChildItem -LiteralPath $ramSource -Force) {
            if ($ramFile.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '연결된 파일은 설치할 수 없습니다.' }
            Copy-Item -LiteralPath $ramFile.FullName -Destination $ramStage -Recurse -Force
        }
        @{product='homesweetlove.RamFlow';version='0.3.2'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ramStage 'RamFlow-install.json') -Encoding utf8
        if (Test-Path -LiteralPath $ramTarget) { Move-Item -LiteralPath $ramTarget -Destination $ramBackup }
        try { Move-Item -LiteralPath $ramStage -Destination $ramTarget } catch { if (Test-Path -LiteralPath $ramBackup) { Move-Item -LiteralPath $ramBackup -Destination $ramTarget }; throw }
        if (Test-Path -LiteralPath $ramBackup) { Remove-Item -LiteralPath $ramBackup -Recurse -Force }
    } finally { if (Test-Path -LiteralPath $ramStage) { Remove-Item -LiteralPath $ramStage -Recurse -Force } }
} else { @{product='homesweetlove.RamFlow';version='0.3.2'} | ConvertTo-Json | Set-Content -LiteralPath $ramMarker -Encoding utf8 }
if (-not $NoIntegration) {
    $ramLinks = @((Join-Path ([Environment]::GetFolderPath('Programs')) 'RamFlow.lnk'))
    if ($DesktopShortcut) { $ramLinks += Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'RamFlow.lnk' }
    foreach ($ramLink in $ramLinks) {
        $ramShortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($ramLink)
        if ((Test-Path -LiteralPath $ramLink) -and $ramShortcut.TargetPath -ne (Join-Path $ramTarget 'RamFlow.exe')) { throw '다른 프로그램의 RamFlow 바로가기를 덮어쓰지 않습니다. 사용자 설치는 완료됐습니다.' }
        $ramShortcut.TargetPath = Join-Path $ramTarget 'RamFlow.exe'; $ramShortcut.WorkingDirectory = $ramTarget
        $ramShortcut.IconLocation = (Join-Path $ramTarget 'RamFlow.exe') + ',0'; $ramShortcut.Save()
    }
    if ($Startup) { New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Force | Out-Null; Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name RamFlow -Value ('"' + (Join-Path $ramTarget 'RamFlow.exe') + '"') }
    if ($MonitorService) {
        $ramPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $ramPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '모니터 서비스 등록은 관리자 권한이 필요합니다. 사용자 설치는 완료됐습니다.' }
        $ramServiceDirectory = Join-Path $env:ProgramData 'RamFlowMonitor'
        if (Test-Path -LiteralPath $ramServiceDirectory) { throw '기존 모니터 상태 폴더를 먼저 제거하세요. 소유권이 불확실한 폴더는 사용하지 않습니다.' }
        New-Item -ItemType Directory -Path $ramServiceDirectory | Out-Null
        $ramAcl = [Security.AccessControl.DirectorySecurity]::new(); $ramAcl.SetAccessRuleProtection($true, $false)
        $ramAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        foreach ($ramSid in @('S-1-5-18','S-1-5-32-544')) { $ramRule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($ramSid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'); $ramAcl.AddAccessRule($ramRule) }
        Set-Acl -LiteralPath $ramServiceDirectory -AclObject $ramAcl
        @{product='homesweetlove.RamFlow';version='0.3.2'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ramServiceDirectory 'RamFlow-monitor-state.json') -Encoding utf8
        # SYSTEM 서비스는 사용자가 쓸 수 없는 Program Files의 독립 복사본으로 실행한다.
        $ramAdminTarget = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'RamFlowMonitor'))
        if (-not $ramAdminTarget.StartsWith([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '서비스 설치 경로 검증 실패' }
        if (Test-Path -LiteralPath $ramAdminTarget) { throw '기존 서비스 설치 폴더를 먼저 제거하세요.' }
        New-Item -ItemType Directory -Path $ramAdminTarget | Out-Null
        Set-Acl -LiteralPath $ramAdminTarget -AclObject $ramAcl
        foreach ($ramFile in Get-ChildItem -LiteralPath $ramTarget -Force) { Copy-Item -LiteralPath $ramFile.FullName -Destination $ramAdminTarget -Recurse -Force }
        $ramBin = '"' + (Join-Path $ramAdminTarget 'RamFlow.Service.exe') + '" --service'
        if (Get-Service -Name RamFlowMonitor -ErrorAction SilentlyContinue) { throw '기존 모니터 서비스를 먼저 제거하세요.' }
        & sc.exe create RamFlowMonitor binPath= $ramBin start= auto DisplayName= 'RamFlow 시스템 모니터'
        if ($LASTEXITCODE -ne 0) { throw '서비스 등록 실패' }; Start-Service RamFlowMonitor
    }
}
Write-Output ('설치 완료: ' + $ramTarget)
if ($Launch) { Start-Process -FilePath (Join-Path $ramTarget 'RamFlow.exe') -WorkingDirectory $ramTarget -WindowStyle Hidden }
