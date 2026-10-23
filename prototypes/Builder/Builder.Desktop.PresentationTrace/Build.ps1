param([string]$OutputDirectory = "$PSScriptRoot/../../../.local/evidence/input-display-20261019/presentation-host")
$ErrorActionPreference='Stop'
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$vsRoot='C:/Program Files/Microsoft Visual Studio/2022/Community'
$compiler='C:/Program Files/Microsoft Visual Studio/2022/Community/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/cl.exe'
$msvc='C:/Program Files/Microsoft Visual Studio/2022/Community/VC/Tools/MSVC/14.44.35207'
$sdk='C:/Program Files (x86)/Windows Kits/10'
$sdkVersion='10.0.26100.0'
$includePaths=@("$msvc/include","$sdk/Include/$sdkVersion/ucrt","$sdk/Include/$sdkVersion/shared","$sdk/Include/$sdkVersion/um","$sdk/Include/$sdkVersion/winrt","$sdk/Include/$sdkVersion/cppwinrt")
$libPaths=@("$msvc/lib/x64","$sdk/Lib/$sdkVersion/ucrt/x64","$sdk/Lib/$sdkVersion/um/x64")
$compilerArgs=@('/nologo','/std:c++20','/EHsc','/O2','/MD','/LD','/W4',"$PSScriptRoot/CaptureObserver.cpp",("/Fo"+(Join-Path $outputPath 'CaptureObserver.obj')))
$compilerArgs+=@($includePaths|ForEach-Object{'/I'+$_})
$compilerArgs+=@('/link',('/OUT:'+(Join-Path $outputPath 'CaptureObserver.dll')),('/IMPLIB:'+(Join-Path $outputPath 'CaptureObserver.lib')),'d3d11.lib','dxgi.lib','windowsapp.lib','user32.lib')
$compilerArgs+=@($libPaths|ForEach-Object{'/LIBPATH:'+$_})
& $compiler @compilerArgs
if($LASTEXITCODE-ne0){throw 'Native diagnostic observer build failed.'}
dotnet build "$PSScriptRoot/Builder.Desktop.PresentationTrace.csproj" -c Release -o $outputPath --nologo
if($LASTEXITCODE-ne0){throw 'Diagnostic host build failed.'}
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $outputPath 'CaptureObserver.dll'),(Join-Path $outputPath 'Builder.dll'),(Join-Path $outputPath 'Builder.Core.dll'),(Join-Path $outputPath 'Builder.Desktop.PresentationTrace.dll') | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'build-hashes.json') -Encoding utf8
