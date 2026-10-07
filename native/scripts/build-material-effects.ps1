param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$sourcePath=(Resolve-Path -LiteralPath $Source).Path
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs){throw 'Material effect metadata requires the installed x64 C++ Build Tools'}
$vcvars=Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
[IO.Directory]::CreateDirectory($outputPath)|Out-Null
$dll=Join-Path $outputPath 'TheOrder.MaterialEffects.dll'
if((Test-Path -LiteralPath $dll) -and (Get-Item -LiteralPath $dll).LastWriteTimeUtc -gt (Get-Item -LiteralPath $sourcePath).LastWriteTimeUtc){return}
$batch=Join-Path $outputPath 'compile.cmd'
$lines=@('@echo off',"call `"$vcvars`" >nul",'if errorlevel 1 exit /b %errorlevel%',"cl /nologo /std:c++17 /EHsc /MD /O2 /W4 /WX /LD `"$sourcePath`" /Fo`"$outputPath\ChannelEffects.obj`" /Fe`"$dll`" /link runtimeobject.lib dxguid.lib /IMPLIB:`"$outputPath\TheOrder.MaterialEffects.lib`"",'exit /b %errorlevel%')
[IO.File]::WriteAllLines($batch,$lines,[Text.Encoding]::Default)
& cmd.exe /d /c $batch
if($LASTEXITCODE -ne 0){throw "Material metadata compile failed ($LASTEXITCODE)"}
