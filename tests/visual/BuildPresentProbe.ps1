param([string]$Sources, [string]$Output, [string]$Label = 'present')
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Sources) { $Sources = Join-Path $project 'src' }
if (!$Output) { $Output = Join-Path $project 'test-output\presentation-probe' }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$sdk = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'dotnet\sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$references = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$arguments = @('/nologo','/noconfig','/nostdlib+','/langversion:7.3','/platform:x64','/optimize+','/warnaserror+')
foreach ($name in @('mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Net.Http','System.IO.Compression','System.Web.Extensions','WindowsBase','UIAutomationClient','UIAutomationTypes','Accessibility')) { $arguments += '/reference:' + (Join-Path $references ($name + '.dll')) }
$executable = Join-Path ([IO.Path]::GetFullPath($Output)) ($Label + '.exe')
$sourcesToCompile = @(Get-ChildItem -LiteralPath $Sources -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
& dotnet (Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll') @arguments '/target:exe' '/main:SnapTransOCR.PresentProbe' ('/out:' + $executable) ('/win32manifest:' + (Join-Path $project 'assets\app.manifest')) @sourcesToCompile (Join-Path $PSScriptRoot 'PresentProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Presentation probe compilation failed.' }
Copy-Item -LiteralPath (Join-Path $project 'assets\SnapTransOCR.exe.config') -Destination ($executable + '.config') -Force
Write-Output $executable
