param([switch]$Test, [switch]$Headless)
$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$sdk = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'dotnet\sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$compiler = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
$references = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (!(Test-Path -LiteralPath $compiler) -or !(Test-Path -LiteralPath $references)) { throw 'Build requires .NET SDK and .NET Framework 4.8 targeting pack.' }
$outputPath = Join-Path $projectPath 'dist'
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$arguments = @('/nologo', '/noconfig', '/nostdlib+', '/langversion:7.3', '/platform:x64', '/optimize+', '/deterministic+', '/warnaserror+')
foreach ($name in @('mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Net.Http','System.IO.Compression','System.Web.Extensions','WindowsBase','UIAutomationClient','UIAutomationTypes','Accessibility')) {
    $arguments += '/reference:' + (Join-Path $references ($name + '.dll'))
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$icon = Join-Path $projectPath 'assets\SnapTransOCR.ico'
if (!(Test-Path -LiteralPath $icon)) { & (Join-Path $projectPath 'make-icon.ps1') }
$executable = Join-Path $outputPath 'SnapTransOCR.exe'
& dotnet $compiler @arguments '/target:winexe' ('/win32manifest:' + (Join-Path $projectPath 'assets\app.manifest')) ('/win32icon:' + $icon) ('/out:' + $executable) @sources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectPath 'assets\SnapTransOCR.exe.config') -Destination $outputPath -Force
Write-Output ('Built ' + $executable)
if ($Test) {
    $testExe = Join-Path $outputPath 'SnapTransOCR.Tests.exe'
    $testSources = @(Get-ChildItem -LiteralPath (Join-Path $projectPath 'tests') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
    & dotnet $compiler @arguments '/target:exe' '/main:SnapTransOCR.Tests' ('/win32manifest:' + (Join-Path $projectPath 'assets\app.manifest')) ('/out:' + $testExe) @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $projectPath 'assets\SnapTransOCR.exe.config') -Destination ($testExe + '.config') -Force
    if ($Headless) { & $testExe --headless } else { & $testExe }
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
