param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$RuntimeVersion='10.0.12',
    [string]$OutputDirectory,
    [string]$CompilerPath,
    [switch]$TestChannel
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot
if(!$Version){$Version=(& (Join-Path $PSScriptRoot 'Test-ReleaseVersion.ps1')).Trim()}
if(!$OutputDirectory){$OutputDirectory=Join-Path $taskRoot ".local/packages/$Version"}
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $taskOutput){throw 'Use a fresh package output directory. Existing packages are never overwritten.'}
if(!$CompilerPath){$CompilerPath=Join-Path $taskRoot '.local/packaging-tools/inno-7.1.0/ISCC.exe'}
if(!(Test-Path -LiteralPath $CompilerPath)){throw 'Supply -CompilerPath for Inno Setup 7.1.0; see docs/windows-packaging.md.'}
$taskCompilerVersion=(& $CompilerPath --version | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $taskCompilerVersion -ne '7.1.0'){throw "Expected pinned Inno Setup 7.1.0; found $taskCompilerVersion"}
$taskBuilder=Join-Path $taskRoot 'src/BuilderCrap'
$taskWorker=Join-Path $taskBuilder 'Builder.GeometryWorker/bin/win-x64/Builder.GeometryWorker.exe'
if(!(Test-Path -LiteralPath $taskWorker)){throw 'Build the native worker with scripts/Build-GeometryWorker.ps1 first.'}
New-Item -ItemType Directory -Path $taskOutput | Out-Null
$taskPayload=Join-Path $taskOutput 'payload'
$taskServer=Join-Path $taskOutput 'mcp-publish'
$taskCommon=@('-c','Release','-r','win-x64','--self-contained','true',"-p:Version=$Version","-p:RuntimeFrameworkVersion=$RuntimeVersion",'-p:TargetLatestRuntimePatch=false','-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:DebugType=None','-p:DebugSymbols=false')
& dotnet publish (Join-Path $taskBuilder 'Builder.Desktop/Builder.Desktop.csproj') @taskCommon -o $taskPayload *> (Join-Path $taskOutput 'publish-desktop.log')
if($LASTEXITCODE -ne 0){throw "Desktop publish failed; see $taskOutput/publish-desktop.log"}
& dotnet publish (Join-Path $taskBuilder 'Builder.Mcp.Server/Builder.Mcp.Server.csproj') @taskCommon -o $taskServer *> (Join-Path $taskOutput 'publish-mcp.log')
if($LASTEXITCODE -ne 0){throw "MCP publish failed; see $taskOutput/publish-mcp.log"}
foreach($taskFile in Get-ChildItem -LiteralPath $taskServer -File -Recurse){
    $taskRelative=[IO.Path]::GetRelativePath($taskServer,$taskFile.FullName)
    $taskDestination=Join-Path $taskPayload $taskRelative
    if(Test-Path -LiteralPath $taskDestination){
        if((Get-FileHash -LiteralPath $taskDestination).Hash -ne (Get-FileHash -LiteralPath $taskFile.FullName).Hash){throw "Conflicting desktop/MCP payload: $taskRelative"}
    }else{
        New-Item -ItemType Directory -Path (Split-Path $taskDestination) -Force | Out-Null
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    }
}
# SDK dependencies and complete runtime/worker license files stay with the actual payload.
$taskLicenses=Join-Path $taskPayload 'licenses'
New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path (Split-Path $CompilerPath) 'LICENSE.TXT') -Destination (Join-Path $taskLicenses 'InnoSetup-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $taskBuilder 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $taskPayload 'THIRD-PARTY-NOTICES.txt')
$taskAssets=Get-Content (Join-Path $taskBuilder 'Builder.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
$taskNuget=($taskAssets.packageFolders.PSObject.Properties | Select-Object -First 1).Name
$taskPackages=@("microsoft.netcore.app.runtime.win-x64/$RuntimeVersion","microsoft.windowsdesktop.app.runtime.win-x64/$RuntimeVersion")
foreach($taskPackage in $taskPackages){
    $taskPackageRoot=Join-Path $taskNuget $taskPackage
    $taskNotices=@(Get-ChildItem -LiteralPath $taskPackageRoot -Recurse -File | Where-Object {$_.Name -match '^(LICENSE(\..*)?|THIRD.PARTY.NOTICES(\..*)?)$'})
    if($taskNotices.Count -eq 0){throw "Missing runtime license: $taskPackage"}
    $taskIndex=0
    foreach($taskNotice in $taskNotices){Copy-Item -LiteralPath $taskNotice.FullName -Destination (Join-Path $taskLicenses (($taskPackage.Split('/')[0])+"-$taskIndex-"+$taskNotice.Name));$taskIndex++}
}
$taskLock=Get-Content (Join-Path $taskBuilder 'Builder.Mcp/packages.lock.json') -Raw | ConvertFrom-Json
$taskDependencies=@{}
foreach($taskTarget in $taskLock.dependencies.PSObject.Properties){foreach($taskDependency in $taskTarget.Value.PSObject.Properties){if($taskDependency.Value.resolved){$taskDependencies[$taskDependency.Name]=$taskDependency.Value.resolved}}}
foreach($taskName in $taskDependencies.Keys){
    $taskNuspec=Get-Content (Join-Path $taskNuget ($taskName.ToLowerInvariant()+'/'+$taskDependencies[$taskName]+'/'+$taskName.ToLowerInvariant()+'.nuspec')) -Raw
    if($taskName -like 'Microsoft.Extensions.*'){
        if($taskNuspec -notmatch '<license type="expression">MIT</license>'){throw "Review changed dependency license for $taskName"}
        Copy-Item -LiteralPath (Join-Path $taskNuget "microsoft.netcore.app.runtime.win-x64/$RuntimeVersion/LICENSE.TXT") -Destination (Join-Path $taskLicenses ($taskName+'-MIT-LICENSE.txt'))
    }
}
Add-Content -LiteralPath (Join-Path $taskPayload 'THIRD-PARTY-NOTICES.txt') -Value "`n# Windows distribution`n`nSelf-contained .NET and Windows Desktop runtime ${RuntimeVersion}: complete runtime licenses/third-party notices are in licenses/. Microsoft.Extensions packages below use the included .NET Foundation MIT license. Inno Setup 7.1.0 builds this installer; its complete license is included. No original Microsoft 3D Builder implementation or assets are included.`n`n$($taskDependencies | ConvertTo-Json)" -Encoding utf8
Set-Content -LiteralPath (Join-Path $taskPayload '.builder-package.ini') -Value "[Package]`nProductId=7C2AC698-9A70-49C8-BF7D-A58F2CF1EB90`nVersion=$Version" -Encoding utf8
if($TestChannel -and $Version -eq '0.1.1'){Set-Content -LiteralPath (Join-Path $taskPayload 'package-update-note.txt') -Value 'Packaging-test-only added file: verifies user collision refusal and rollback of a newly added owned file.' -Encoding utf8}
foreach($taskName in @('Builder.exe','Builder.dll','Builder.Mcp.Server.exe','Builder.Mcp.Server.dll','Builder.GeometryWorker.exe','coreclr.dll','hostfxr.dll','PresentationFramework.dll','Help/index.html','licenses/Manifold-LICENSE.txt','licenses/ModelContextProtocol-LICENSE.txt')){
    if(!(Test-Path -LiteralPath (Join-Path $taskPayload $taskName))){throw "Required payload missing: $taskName"}
}
foreach($taskName in @('Builder.runtimeconfig.json','Builder.Mcp.Server.runtimeconfig.json')){
    $taskConfig=Get-Content (Join-Path $taskPayload $taskName) -Raw | ConvertFrom-Json
    if(!$taskConfig.runtimeOptions.includedFrameworks -or $taskConfig.runtimeOptions.framework -or $taskConfig.runtimeOptions.frameworks){throw "$taskName is not self-contained"}
}
$taskOwnedFiles=@(Get-ChildItem -LiteralPath $taskPayload -Recurse -File | ForEach-Object {[IO.Path]::GetRelativePath($taskPayload,$_.FullName)})+@('package-files.txt','release-manifest.json')
$taskOwnedFiles | Sort-Object -Unique | Set-Content -LiteralPath (Join-Path $taskPayload 'package-files.txt') -Encoding utf8
$taskManifest=[ordered]@{product='3D BuilderCrap';version=$Version;rid='win-x64';runtimeVersion=$RuntimeVersion;compilerVersion=$taskCompilerVersion;testChannel=[bool]$TestChannel;dependencies=$taskDependencies;files=@()}
$taskManifest.files=@(Get-ChildItem -LiteralPath $taskPayload -Recurse -File | Sort-Object FullName | ForEach-Object {@{path=[IO.Path]::GetRelativePath($taskPayload,$_.FullName).Replace('\','/');length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskPayload 'release-manifest.json') -Encoding utf8
$taskCompile=@("--define=PayloadDir=$taskPayload","--define=PackageVersion=$Version","--output-dir=$taskOutput")
if($TestChannel){$taskCompile+=@('--define=PackageAppId=B5AAFA7B-3ACF-44FC-AAD4-1A3B1DC6587A','--define=PackageName=3D BuilderCrap Packaging Test')}
& $CompilerPath @taskCompile (Join-Path $taskBuilder 'Packaging/Builder.iss') *> (Join-Path $taskOutput 'compiler.log')
if($LASTEXITCODE -ne 0){throw "Installer compilation failed; see $taskOutput/compiler.log"}
$taskSetups=@(Get-ChildItem -LiteralPath $taskOutput -Filter '*-Setup.exe' -File)
if($taskSetups.Count -ne 1){throw 'Expected exactly one installer output.'}
$taskSetup=$taskSetups[0]
$taskHash=(Get-FileHash -LiteralPath $taskSetup.FullName).Hash
"$taskHash  $($taskSetup.Name)" | Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt')
Write-Output "Package ready: $($taskSetup.FullName)"
Write-Output "SHA-256: $taskHash"
