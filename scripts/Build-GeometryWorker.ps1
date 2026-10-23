param([string]$CMakePath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot
$taskSource = Join-Path $taskRoot 'src/BuilderCrap/Builder.GeometryWorker'
$taskBuild = Join-Path $taskRoot '.local/geometry-build/BuilderCrap'
if (!$CMakePath) {
    $taskCmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
    if ($taskCmakeCommand) { $CMakePath = $taskCmakeCommand.Source }
    else {
        $taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $taskVs = & $taskVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.CMake.Project -property installationPath
        $CMakePath = Join-Path $taskVs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    }
}
if (!(Test-Path -LiteralPath $CMakePath)) { throw 'Install Visual Studio C++ build tools with CMake, or supply -CMakePath.' }
# Pinned upstream sources; CMake verifies/checks out this exact commit. No runtime downloads.
& $CMakePath -S $taskSource -B $taskBuild -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { throw 'Geometry configuration failed.' }
& $CMakePath --build $taskBuild --config Release --target Builder.GeometryWorker Builder.WorkerFaultProbe --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Geometry build failed.' }
$taskNotices = Join-Path $taskSource 'bin/win-x64/licenses'
New-Item -ItemType Directory -Force $taskNotices | Out-Null
Copy-Item -LiteralPath (Join-Path $taskBuild '_deps/manifold-src/LICENSE') -Destination (Join-Path $taskNotices 'Manifold-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $taskBuild '_deps/manifold-src/AUTHORS') -Destination (Join-Path $taskNotices 'Manifold-AUTHORS.txt')
Write-Output 'Geometry worker ready. Build Builder.slnx to copy it beside the desktop app and checks.'
