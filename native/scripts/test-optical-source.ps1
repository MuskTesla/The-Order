param([Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$Packages,[switch]$CompileOnly)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(!$output.StartsWith($repo+'\.artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Isolated workspace artifact output required'}
$include=Join-Path $Packages 'microsoft.windowsappsdk.winui/2.3.9/include'
if(!(Test-Path (Join-Path $include 'microsoft.ui.xaml.media.dxinterop.h'))){throw 'Locked WinUI header required'}
$vswhere=Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs){throw 'Installed x64 C++ Build Tools required'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$source=Join-Path $repo 'native/fixtures/optical-source-regression.cpp'
$batch=Join-Path $output 'compile.cmd'
$lines=@('@echo off',('call "{0}" >nul' -f (Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat')),'if errorlevel 1 exit /b %errorlevel%',
 ('cl /nologo /utf-8 /std:c++17 /EHsc /MD /O2 /W4 /WX /I"{0}" "{1}" /Fo"{2}/test.obj" /Fe"{2}/test.exe" /link d3d11.lib dxgi.lib d3dcompiler.lib d2d1.lib dwrite.lib windowscodecs.lib ole32.lib' -f $include,$source,$output),'exit /b %errorlevel%')
[IO.File]::WriteAllLines($batch,$lines,[Text.Encoding]::Default)
& cmd.exe /d /c $batch
if($LASTEXITCODE -ne 0){throw 'Optical source regression compilation failed'}
if(!$CompileOnly){& (Join-Path $output 'test.exe') Test;if($LASTEXITCODE -ne 0){throw 'Optical source regression failed'}}
