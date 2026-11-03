param([string]$Output)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Output) { $Output = Join-Path $project 'test-output\scroll-probe' }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$sdk = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'dotnet\sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$references = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$arguments = @('/nologo','/noconfig','/nostdlib+','/langversion:7.3','/platform:x64','/optimize+','/warnaserror+')
foreach ($name in @('mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Web.Extensions')) { $arguments += '/reference:' + (Join-Path $references ($name + '.dll')) }
$executable = Join-Path ([IO.Path]::GetFullPath($Output)) 'ScrollProbe.exe'
& dotnet (Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll') @arguments '/target:exe' ('/out:' + $executable) ('/win32manifest:' + (Join-Path $project 'assets\app.manifest')) (Join-Path $PSScriptRoot 'ScrollProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Scroll probe compilation failed.' }
Copy-Item -LiteralPath (Join-Path $project 'assets\SnapTransOCR.exe.config') -Destination ($executable + '.config') -Force
Write-Output $executable
