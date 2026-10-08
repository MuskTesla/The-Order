param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$Packages)
$ErrorActionPreference='Stop'
$sourcePath=(Resolve-Path -LiteralPath $Source).Path
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
$include=Join-Path $Packages 'microsoft.windowsappsdk.winui/2.3.9/include'
if(!(Test-Path -LiteralPath (Join-Path $include 'microsoft.ui.xaml.media.dxinterop.h'))){throw 'Locked WinUI 2.3.9 interop header missing'}
$vswhere=Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs){throw 'Installed x64 C++ Build Tools required'}
$vcvars=Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
[IO.Directory]::CreateDirectory($outputPath)|Out-Null
$dll=Join-Path $outputPath 'TheOrder.OpticalMaterial.dll'
$sourceTime=(Get-Item -LiteralPath $sourcePath).LastWriteTimeUtc
$header=Join-Path (Split-Path $sourcePath) 'GallerySource.h'
if(Test-Path -LiteralPath $header){$sourceTime=@($sourceTime,(Get-Item -LiteralPath $header).LastWriteTimeUtc)|Sort-Object -Descending|Select-Object -First 1}
if((Test-Path -LiteralPath $dll) -and (Get-Item -LiteralPath $dll).LastWriteTimeUtc -gt $sourceTime){return}
$batch=Join-Path $outputPath 'compile-optical.cmd'
$command='cl /nologo /utf-8 /std:c++17 /EHsc /MD /O2 /W4 /WX /LD /I"{0}" "{1}" /Fo"{2}\OpticalRenderer.obj" /Fe"{3}" /link d3d11.lib dxgi.lib d3dcompiler.lib d2d1.lib dwrite.lib windowscodecs.lib ole32.lib /IMPLIB:"{2}\TheOrder.OpticalMaterial.lib"' -f $include,$sourcePath,$outputPath,$dll
$lines=@('@echo off',('call "{0}" >nul' -f $vcvars),'if errorlevel 1 exit /b %errorlevel%',$command,'exit /b %errorlevel%')
[IO.File]::WriteAllLines($batch,$lines,[Text.Encoding]::Default)
& cmd.exe /d /c $batch
if($LASTEXITCODE -ne 0){throw "Optical material compile failed ($LASTEXITCODE)"}
