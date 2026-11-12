param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\SnapTransOCR'),
    [switch]$NoLaunch,
    [switch]$NoIntegration
)
$ErrorActionPreference = 'Stop'
$sourcePath = [IO.Path]::GetFullPath($PSScriptRoot)
$targetPath = [IO.Path]::GetFullPath($Destination).TrimEnd([char[]]@('\','/'))
$targetRoot = [IO.Path]::GetPathRoot($targetPath).TrimEnd([char[]]@('\','/'))
if ($targetPath -eq $targetRoot -or $targetPath.Contains('"') -or
    [IO.Path]::GetFileName($targetPath) -in @('Program Files','Program Files (x86)','ProgramFiles')) { throw '请选择 SnapTransOCR 专用目录，不能使用磁盘或 Program Files 根目录。' }
if ((Test-Path -LiteralPath $targetPath) -and ((Get-Item -LiteralPath $targetPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '安装目录不能是重解析点。' }
if ((Test-Path -LiteralPath $targetPath) -and !(Test-Path -LiteralPath $targetPath -PathType Container)) { throw '安装目录位置已被文件占用。' }
$targetExe = Join-Path $targetPath 'SnapTransOCR.exe'
$running = @(Get-Process -Name SnapTransOCR -ErrorAction SilentlyContinue)
foreach ($process in $running) {
    if (!$process.Path -or ![String]::Equals($process.Path, $targetExe, [StringComparison]::OrdinalIgnoreCase)) { throw '另一个安装目录的 SnapTransOCR 正在运行，请先从托盘退出后再安装。' }
}
$binaryPath = if (Test-Path -LiteralPath (Join-Path $sourcePath 'SnapTransOCR.exe')) { $sourcePath } else { Join-Path $sourcePath 'dist' }
$payload = [ordered]@{
    'SnapTransOCR.exe.config' = Join-Path $binaryPath 'SnapTransOCR.exe.config'
    'LICENSE' = Join-Path $sourcePath 'LICENSE'
    'NOTICE.md' = Join-Path $sourcePath 'NOTICE.md'
    'LICENSE-Apache-2.0.txt' = if (Test-Path -LiteralPath (Join-Path $sourcePath 'LICENSE-Apache-2.0.txt')) { Join-Path $sourcePath 'LICENSE-Apache-2.0.txt' } else { Join-Path $sourcePath 'docs\LICENSE-Apache-2.0.txt' }
    'install.ps1' = $PSCommandPath
    'SnapTransOCR.exe' = Join-Path $binaryPath 'SnapTransOCR.exe'
}
foreach ($file in $payload.Values) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw ('缺少安装文件：' + $file) } }
$sourceData = Join-Path $sourcePath 'data'
$targetData = Join-Path $targetPath 'data'
$copyData = (Test-Path -LiteralPath $sourceData -PathType Container) -and !(Test-Path -LiteralPath $targetData)
if ($copyData -and @(Get-ChildItem -LiteralPath $sourceData -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw '源 data 中存在重解析点，请先单独整理该目录。' }
$parentPath = [IO.Path]::GetDirectoryName($targetPath)
$stagePath = Join-Path $parentPath ('.SnapTransOCR-install-' + [Guid]::NewGuid().ToString('N'))
$stagePayload = Join-Path $stagePath 'payload'
$stageBackup = Join-Path $stagePath 'backup'
$originalFiles = @{}
$targetChanged = $false
$dataCopied = $false
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'SnapTransOCR.lnk'
$originalShortcut = if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) { [IO.File]::ReadAllBytes($shortcutPath) } else { $null }
$shortcutWrittenHash = $null
$runPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$runCommand = '"' + $targetExe + '" --tray'
$previousRun = $null
$previousRunKind = [Microsoft.Win32.RegistryValueKind]::String
$autoStartUpdated = $false
$runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runPath)
try { if ($runKey) { $previousRun = $runKey.GetValue('SnapTransOCR', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -as [string]; if ($null -ne $previousRun) { $previousRunKind = $runKey.GetValueKind('SnapTransOCR') } } } finally { if ($runKey) { $runKey.Dispose() } }
try {
    New-Item -ItemType Directory -Path $stagePayload,$stageBackup -Force | Out-Null
    foreach ($name in $payload.Keys) { Copy-Item -LiteralPath $payload[$name] -Destination (Join-Path $stagePayload $name) }
    $noticePath = Join-Path $stagePayload 'NOTICE.md'
    [IO.File]::WriteAllText($noticePath, [IO.File]::ReadAllText($noticePath).Replace('docs/LICENSE-Apache-2.0.txt', 'LICENSE-Apache-2.0.txt'), [Text.UTF8Encoding]::new($false))
    $sourceUsage = Join-Path $sourcePath '使用说明.txt'
    if (Test-Path -LiteralPath $sourceUsage -PathType Leaf) { Copy-Item -LiteralPath $sourceUsage -Destination $stagePayload }
    else { Set-Content -LiteralPath (Join-Path $stagePayload '使用说明.txt') -Encoding utf8 -Value 'SnapTransOCR：Alt+1 截图，Ctrl+Shift+A 划词翻译，Ctrl+Shift+S 历史/剪贴板。右键托盘打开设置。历史优先保存在 data\history，API 配置保存在 %LOCALAPPDATA%\SnapTransOCR\settings.json。' }
    if ($copyData) {
        Copy-Item -LiteralPath $sourceData -Destination $stagePayload -Recurse
        $stagedData = Join-Path $stagePayload 'data'
        foreach ($file in Get-ChildItem -LiteralPath $sourceData -File -Force -Recurse) {
            $relative = $file.FullName.Substring($sourceData.Length).TrimStart([char[]]@('\','/'))
            if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $stagedData $relative)).Hash) { throw ('data 复制校验失败：' + $relative) }
        }
    }
    foreach ($process in $running) {
        Start-Process -FilePath $targetExe -ArgumentList '--quit' -WindowStyle Hidden -Wait
        if (!$process.WaitForExit(10000)) { throw '当前程序未正常退出，安装文件尚未替换。' }
    }
    $runtimeFiles = @(Get-ChildItem -LiteralPath $stagePayload -File)
    foreach ($file in $runtimeFiles) {
        $path = Join-Path $targetPath $file.Name
        $targetItem = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        if ($targetItem -and ($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('安装文件不能是重解析点：' + $file.Name) }
        if ($targetItem -and $targetItem.PSIsContainer) { throw ('安装文件位置被目录占用：' + $file.Name) }
        $originalFiles[$file.Name] = $null -ne $targetItem
        if ($originalFiles[$file.Name]) { Copy-Item -LiteralPath $path -Destination $stageBackup }
    }
    New-Item -ItemType Directory -Path $targetPath -Force | Out-Null
    $targetChanged = $true
    foreach ($file in $runtimeFiles) {
        $path = Join-Path $targetPath $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $path -Force
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $path).Hash) { throw ('运行文件校验失败：' + $file.Name) }
    }
    if ($copyData -and !(Test-Path -LiteralPath $targetData)) {
        $stagedData = (Resolve-Path -LiteralPath (Join-Path $stagePayload 'data')).Path
        if (![String]::Equals($stagedData, (Join-Path $stagePayload 'data'), [StringComparison]::OrdinalIgnoreCase) -or ![String]::Equals([IO.Path]::GetDirectoryName($targetData), $targetPath, [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝移动安装暂存目录以外的数据。' }
        [IO.Directory]::Move($stagedData, $targetData)
        $dataCopied = $true
    }
    if (!$NoIntegration) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($shortcutPath)) -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $null
        try {
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $targetExe; $shortcut.WorkingDirectory = $targetPath; $shortcut.Arguments = '--tray'; $shortcut.IconLocation = $targetExe + ',0'; $shortcut.Description = 'SnapTransOCR 截图、OCR、翻译与剪贴板历史'; $shortcut.Save()
            $shortcutWrittenHash = (Get-FileHash -LiteralPath $shortcutPath).Hash
        } finally { if ($shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }; [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
        if (![String]::IsNullOrWhiteSpace($previousRun)) {
            $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runPath, $true)
            try { if ($runKey) { $runKey.SetValue('SnapTransOCR', $runCommand, [Microsoft.Win32.RegistryValueKind]::String); $autoStartUpdated = $true } } finally { if ($runKey) { $runKey.Dispose() } }
        }
    }
    if (!$NoLaunch) { Start-Process -FilePath $targetExe -ArgumentList '--tray' -WorkingDirectory $targetPath -WindowStyle Hidden | Out-Null }
    [pscustomobject]@{ InstalledPath = $targetPath; Executable = $targetExe; Version = (Get-Item -LiteralPath $targetExe).VersionInfo.FileVersion; DataCopied = $dataCopied; ExistingDataPreserved = $true; Shortcut = if ($NoIntegration) { $null } else { $shortcutPath }; AutoStartUpdated = $autoStartUpdated; IntegrationSkipped = [bool]$NoIntegration; Launched = !$NoLaunch } | ConvertTo-Json
}
catch {
    $failure = $_
    if ($targetChanged) {
        foreach ($name in $originalFiles.Keys) {
            $path = Join-Path $targetPath $name
            if ($originalFiles[$name]) { Copy-Item -LiteralPath (Join-Path $stageBackup $name) -Destination $path -Force }
            elseif (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        }
    }
    if ($shortcutWrittenHash -and (Test-Path -LiteralPath $shortcutPath) -and (Get-FileHash -LiteralPath $shortcutPath).Hash -eq $shortcutWrittenHash) {
        if ($null -ne $originalShortcut) { [IO.File]::WriteAllBytes($shortcutPath, $originalShortcut) } else { Remove-Item -LiteralPath $shortcutPath -Force }
    }
    if ($autoStartUpdated) {
        $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runPath, $true)
        try { if ($runKey -and $runKey.GetValue('SnapTransOCR') -eq $runCommand) { $runKey.SetValue('SnapTransOCR', $previousRun, $previousRunKind) } } finally { if ($runKey) { $runKey.Dispose() } }
    }
    if ($running.Count -gt 0 -and !$NoLaunch -and (Test-Path -LiteralPath $targetExe)) { Start-Process -FilePath $targetExe -ArgumentList '--tray' -WorkingDirectory $targetPath -WindowStyle Hidden | Out-Null }
    throw $failure
}
finally {
    if (Test-Path -LiteralPath $stagePath) {
        $resolvedStage = (Resolve-Path -LiteralPath $stagePath).Path
        if (![String]::Equals($resolvedStage, $stagePath, [StringComparison]::OrdinalIgnoreCase) -or ![String]::Equals([IO.Path]::GetDirectoryName($resolvedStage), $parentPath, [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝清理安装暂存目录以外的路径。' }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
