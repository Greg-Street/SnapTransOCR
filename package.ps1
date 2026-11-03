param([string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'output\SnapTransOCR-0.2.20'))
$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$portableName = 'SnapTransOCR-0.2.20-win-x64'
$destinationPath = [IO.Path]::GetFullPath($Destination)
$portablePath = Join-Path $destinationPath $portableName
$stagingPath = Join-Path $destinationPath ('.package-' + [Guid]::NewGuid().ToString('N'))
$stagedPortable = Join-Path $stagingPath $portableName
New-Item -ItemType Directory -Path $stagedPortable -Force | Out-Null
try {
    foreach ($name in @('SnapTransOCR.exe','SnapTransOCR.exe.config')) {
        Copy-Item -LiteralPath (Join-Path $projectPath ('dist\' + $name)) -Destination $stagedPortable
    }
    foreach ($name in @('LICENSE','NOTICE.md')) {
        Copy-Item -LiteralPath (Join-Path $projectPath $name) -Destination $stagedPortable
    }
    $noticePath = Join-Path $stagedPortable 'NOTICE.md'
    $notice = [IO.File]::ReadAllText($noticePath).Replace('docs/LICENSE-Apache-2.0.txt', 'LICENSE-Apache-2.0.txt')
    [IO.File]::WriteAllText($noticePath, $notice, [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $projectPath 'docs\LICENSE-Apache-2.0.txt') -Destination (Join-Path $stagedPortable 'LICENSE-Apache-2.0.txt')
    $installScript = Join-Path $projectPath 'install.ps1'
    if (Test-Path -LiteralPath $installScript) { Copy-Item -LiteralPath $installScript -Destination $stagedPortable }
    $usage = @'
SnapTransOCR

运行 SnapTransOCR.exe，程序默认留在系统托盘。
Alt+1：截图、标注、OCR 和翻译。
Ctrl+Shift+A：划词翻译。Ctrl+Alt+V：历史 / 剪贴板。
右键托盘打开设置，按需填写并启用自己的 API 服务。

固定安装目录：%LOCALAPPDATA%\Programs\SnapTransOCR。
如包内提供 install.ps1，可在 PowerShell 中运行 .\install.ps1 安装或更新。
历史数据优先保存在程序目录 data\history；移动程序时请一并保留 data。
常规设置 → 历史保存天数：直接输入保存天数，默认 7 天，保存后生效。
API 配置保存在 %LOCALAPPDATA%\SnapTransOCR\settings.json。

完整使用说明、接口资料和验证记录在单独的源码包 README.md 和 docs 中。
运行包只保留程序、必要配置、许可和本说明，不包含测试工具、日志或个人数据。
'@
    Set-Content -LiteralPath (Join-Path $stagedPortable '使用说明.txt') -Value $usage -Encoding utf8
    $portableZip = Join-Path $destinationPath ($portableName + '.zip')
    # Always archive the fresh whitelist, never an existing folder that may contain history or old documentation.
    Compress-Archive -LiteralPath $stagedPortable -DestinationPath $portableZip -Force
    if ((Test-Path -LiteralPath $portablePath) -and ((Get-Item -LiteralPath $portablePath).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Portable output directory must not be a reparse point.' }
    New-Item -ItemType Directory -Force -Path $portablePath | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $stagedPortable -File) { Copy-Item -LiteralPath $file.FullName -Destination $portablePath -Force }
    $sourceZip = Join-Path $destinationPath 'SnapTransOCR-0.2.20-source.zip'
    $sourceNames = @('src','tests','assets','docs','build.ps1','make-icon.ps1','package.ps1','README.md','LICENSE','NOTICE.md','.gitignore')
    if (Test-Path -LiteralPath $installScript) { $sourceNames += 'install.ps1' }
    $sourceItems = $sourceNames | ForEach-Object { Join-Path $projectPath $_ }
    Compress-Archive -LiteralPath $sourceItems -DestinationPath $sourceZip -Force
    Get-FileHash -LiteralPath $portableZip,$sourceZip -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + (Split-Path $_.Path -Leaf) } | Set-Content -LiteralPath (Join-Path $destinationPath 'SHA256SUMS.txt')
    Get-Item -LiteralPath $portableZip,$sourceZip | Select-Object FullName,Length
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        $resolvedStaging = (Resolve-Path -LiteralPath $stagingPath).Path
        if (![String]::Equals($resolvedStaging, $stagingPath, [StringComparison]::OrdinalIgnoreCase) -or
            ![String]::Equals([IO.Path]::GetDirectoryName($resolvedStaging).TrimEnd([char[]]@('\','/')), $destinationPath.TrimEnd([char[]]@('\','/')), [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the package staging directory.' }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
