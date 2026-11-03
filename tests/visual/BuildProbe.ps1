param(
    [string]$Sources,
    [string]$Label = 'current',
    [string]$Output
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Sources) { $Sources = Join-Path $project 'src' }
if (!$Output) { $Output = Join-Path $project 'test-output\resize-probe' }
$sourceDirectory = (Resolve-Path -LiteralPath $Sources).Path
$sdk = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'dotnet\sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$references = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$arguments = @('/nologo','/noconfig','/nostdlib+','/langversion:7.3','/platform:x64','/optimize+','/warnaserror+')
foreach ($name in @('mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Net.Http','System.IO.Compression','System.Web.Extensions','WindowsBase','UIAutomationClient','UIAutomationTypes','Accessibility')) {
    $arguments += '/reference:' + (Join-Path $references ($name + '.dll'))
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$executable = Join-Path ([IO.Path]::GetFullPath($Output)) ($Label + '-probe.exe')
$sourcesToCompile = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
& dotnet (Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll') @arguments '/target:exe' '/main:SnapTransOCR.PassiveResizeProbe' ('/out:' + $executable) ('/win32manifest:' + (Join-Path $project 'assets\app.manifest')) @sourcesToCompile (Join-Path $PSScriptRoot 'PassiveResizeProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed.' }
Copy-Item -LiteralPath (Join-Path $project 'assets\SnapTransOCR.exe.config') -Destination ($executable + '.config') -Force
Write-Output $executable
