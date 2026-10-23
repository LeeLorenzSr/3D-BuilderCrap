param([Parameter(Mandatory)][string]$BasePackage,[Parameter(Mandatory)][string]$UpdatePackage,[Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
foreach($taskPackagePath in @($BasePackage,$UpdatePackage)){
    if((Get-Item -LiteralPath $taskPackagePath).VersionInfo.ProductName.Trim() -ne '3D BuilderCrap Packaging Test'){throw 'Only isolated Packaging Test installers may be executed by this harness.'}
}
$taskEvidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $taskEvidence){throw 'Choose a fresh evidence directory.'}
New-Item -ItemType Directory -Path $taskEvidence | Out-Null
$taskInstall=Join-Path $taskEvidence 'installed'
$taskData=Join-Path $taskEvidence 'user-data'
New-Item -ItemType Directory -Path $taskData | Out-Null
$taskRegistry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\B5AAFA7B-3ACF-44FC-AAD4-1A3B1DC6587A_is1'
$taskIdentityRegistry='HKCU:\Software\3DBuilderCrap\Installations\B5AAFA7B-3ACF-44FC-AAD4-1A3B1DC6587A'
if(Test-Path -LiteralPath $taskRegistry){throw 'The isolated packaging-test registration is already present; inspect it before rerunning.'}
$taskPassed=New-Object 'Collections.Generic.List[string]'
$taskEvents=New-Object 'Collections.Generic.List[object]'
$script:taskClient=$null
$taskEnvironment=[Environment]::GetEnvironmentVariable('LOCALAPPDATA')
$taskProfile=Join-Path $taskEnvironment '3D BuilderCrap'
$taskProfileBefore=@{}
foreach($taskName in @('view-preferences.json','recent-files.json')){$taskPath=Join-Path $taskProfile $taskName;if(Test-Path -LiteralPath $taskPath){$taskProfileBefore[$taskName]=(Get-FileHash -LiteralPath $taskPath).Hash}}
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Pass([string]$Message){$taskPassed.Add($Message);Write-Output "PASS $Message"}
function Setup([string]$Exe,[string]$Label,[string]$Destination=$taskInstall){
    $log=Join-Path $taskEvidence ($Label+'.log')
    $p=Start-Process -FilePath $Exe -WindowStyle Hidden -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$Destination`" /LOG=`"$log`"" -PassThru -Wait
    $taskEvents.Add(@{action=$Label;exitCode=$p.ExitCode;log=$log});return $p.ExitCode
}
function Uninstall([string]$Label){
    $log=Join-Path $taskEvidence ($Label+'.log')
    $p=Start-Process -FilePath (Join-Path $taskInstall 'unins000.exe') -WindowStyle Hidden -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$log`"" -PassThru -Wait
    $taskEvents.Add(@{action=$Label;exitCode=$p.ExitCode;log=$log});return $p.ExitCode
}
function StartClient([switch]$Recovery){
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=Join-Path $taskInstall 'Builder.Mcp.Server.exe';$info.Arguments='--headless'
    if($Recovery){$info.Arguments+=' --recovery "'+(Join-Path $taskData 'Recovery')+'"'}
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.EnvironmentVariables['DOTNET_ROOT']=Join-Path $taskEvidence 'no-global-runtime'
    $info.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP']='0'
    $info.EnvironmentVariables['PATH']=Join-Path $env:WINDIR 'System32'
    $script:taskClient=[Diagnostics.Process]::Start($info)
    $script:taskErrorRead=$script:taskClient.StandardError.ReadToEndAsync()
    $script:taskRequest=0
    $init=Rpc 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='package-verification';version='1'}}
    $script:taskClient.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    return $init
}
function Rpc([string]$Method,$Parameters){
    $script:taskRequest++
    $id=$script:taskRequest
    $script:taskClient.StandardInput.WriteLine((@{jsonrpc='2.0';id=$id;method=$Method;params=$Parameters}|ConvertTo-Json -Depth 50 -Compress))
    do{
        $read=$script:taskClient.StandardOutput.ReadLineAsync()
        Check ($read.Wait(30000)) "MCP timeout: $Method"
        Check ($null -ne $read.Result) "MCP closed during $Method"
        $reply=$read.Result|ConvertFrom-Json
    }while($reply.id -ne $id)
    if($reply.error){throw ($reply.error|ConvertTo-Json -Compress)}
    return $reply.result
}
function Call([string]$Name,$Arguments=@{},[string]$Outcome='Success'){
    $reply=Rpc 'tools/call' @{name=$Name;arguments=$Arguments}
    $result=$reply.structuredContent
    Check ($result.result.outcome -eq $Outcome) "$Name expected $Outcome : $($result.result.message)"
    return $result
}
function CloseClient{
    if($script:taskClient){
        $script:taskClient.StandardInput.Close()
        if(!$script:taskClient.WaitForExit(10000)){$script:taskClient.Kill();throw 'Owned MCP process did not stop on stdin EOF.'}
        $stderr=$script:taskErrorRead.Result
        Check ($script:taskClient.ExitCode -eq 0) "MCP process exit: $stderr"
        $script:taskClient.Dispose();$script:taskClient=$null
    }
}
function Inventory{
    $manifest=Get-Content (Join-Path $taskInstall 'release-manifest.json') -Raw|ConvertFrom-Json
    foreach($item in $manifest.files){Check ((Get-FileHash -LiteralPath (Join-Path $taskInstall $item.path)).Hash -eq $item.sha256) "Payload changed: $($item.path)"}
    return $manifest
}
try{
    $foreign=Join-Path $taskEvidence 'foreign';New-Item -ItemType Directory -Path $foreign|Out-Null
    Set-Content (Join-Path $foreign 'keep.txt') 'unrelated data'
    Check ((Setup $BasePackage 'foreign-refusal' $foreign) -ne 0) 'Foreign nonempty destination accepted'
    Check ((Get-Content (Join-Path $foreign 'keep.txt') -Raw).Trim() -eq 'unrelated data') 'Foreign data changed'
    Check (!(Test-Path -LiteralPath $taskRegistry)) 'Failed install registered itself'
    Pass 'A foreign nonempty destination is refused before file/registry changes'
    Check ((Setup $BasePackage 'install') -eq 0) 'Initial install failed'
    $manifest=Inventory
    Check ($manifest.testChannel -eq $true) 'Use packaging-test installers, never the production registration'
    Check ($manifest.version -eq '0.1.0') 'Expected test base version0.1.0'
    $installId=(Get-ItemProperty -LiteralPath $taskIdentityRegistry).Id
    Check ($installId.Length -eq 64) 'Missing installation identity'
    $version=(& (Join-Path $taskInstall 'Builder.Mcp.Server.exe') --version)|ConvertFrom-Json
    Check ($version.runtimeDirectory.StartsWith($taskInstall,[StringComparison]::OrdinalIgnoreCase)) 'Installed host uses an external runtime'
    Check ($version.architecture -eq 'X64') 'Unexpected runtime architecture'
    Pass 'Fresh per-user install contains its complete hashed payload; installed launcher loads its bundled x64 runtime'
    $init=StartClient -Recovery
    Check ($init.serverInfo.version -eq '0.1.0') 'MCP version not sourced from packaged assembly'
    $tools=Rpc 'tools/list' @{};Check ($tools.tools.Count -eq 71) 'Packaged tool catalog incomplete'
    $scene=Call 'builder_add_cube' @{edit=@{};expectedRevision=0}
    $id=$scene.state.primaryObjectId
    $preview=Call 'builder_preview_hollow' @{request=@{objectId=$id;wallThicknessMillimeters=2};expectedRevision=$scene.state.revision}
    $scene=Call 'builder_apply_preview' @{previewId=$preview.state.preview.id;expectedRevision=$preview.state.revision}
    Check ((Setup $UpdatePackage 'running-update-refusal') -ne 0) 'Update accepted a running MCP host'
    Check ((Uninstall 'running-uninstall-refusal') -ne 0) 'Uninstall accepted a running MCP host'
    $observed=Call 'builder_scene';Check ($observed.state.contentId -eq $scene.state.contentId) 'Maintenance changed the running scene'
    Pass 'Update/uninstall refuse a real running MCP host without closing it or changing its accepted scene'
    $preview=Call 'builder_preview_text' @{request=@{source=@{sourceKind='font';specification=@{text='Print';fontFamily='Arial';heightMillimeters=6}};depthMillimeters=1};expectedRevision=$scene.state.revision}
    $null=Call 'builder_cancel_preview' @{previewId=$preview.state.preview.id;expectedRevision=$scene.state.revision} 'Cancelled'
    $saved=Join-Path $taskInstall 'user-model.3mf'
    $scene=Call 'builder_save' @{path=$saved;expectedRevision=$scene.state.revision}
    $null=Call 'builder_export' @{path=(Join-Path $taskData 'printed.stl');unit='Millimeters';expectedRevision=$scene.state.revision}
    $preview=Call 'builder_preview_move' @{request=@{objectId=$id;deltaMillimeters=@{x=3;y=0;z=0}};expectedRevision=$scene.state.revision}
    $scene=Call 'builder_apply_preview' @{previewId=$preview.state.preview.id;expectedRevision=$scene.state.revision}
    $null=Call 'builder_recovery_checkpoint' @{expectedRevision=$scene.state.revision}
    CloseClient
    $recoveryFiles=@(Get-ChildItem (Join-Path $taskData 'Recovery') -Recurse -File)
    Check ($recoveryFiles.Count -gt 0) 'No preserved recovery checkpoint'
    $modelHash=(Get-FileHash -LiteralPath $saved).Hash
    Pass 'Installed MCP exposes71 tools and runs real worker hollowing, installed-font preview, save/export and separate recovery checkpointing'
    $gate=New-Object Threading.Mutex($false,('Global\3DBuilderCrap-7C2AC698-9A70-49C8-BF7D-A58F2CF1EB90-'+$installId.ToUpperInvariant()+'-Maintenance'))
    Check ($gate.WaitOne(0)) 'Could not reserve maintenance gate'
    try{
        $p=Start-Process -FilePath (Join-Path $taskInstall 'Builder.Mcp.Server.exe') -ArgumentList '--version' -WindowStyle Hidden -Wait -PassThru -RedirectStandardError (Join-Path $taskEvidence 'maintenance-stderr.txt') -RedirectStandardOutput (Join-Path $taskEvidence 'maintenance-stdout.txt')
        Check ($p.ExitCode -ne 0 -and (Get-Content (Join-Path $taskEvidence 'maintenance-stderr.txt') -Raw) -like '*being installed or removed*') 'Host launched through held maintenance gate'
    }finally{$gate.ReleaseMutex();$gate.Dispose()}
    Pass 'A new installed host refuses startup while the actual per-installation maintenance gate is held'
    $collision=Join-Path $taskInstall 'package-update-note.txt';Set-Content $collision 'user-added file'
    Check ((Setup $UpdatePackage 'user-collision-refusal') -ne 0) 'Update overwrote a user-added incoming filename'
    Check ((Get-Content $collision -Raw).Trim() -eq 'user-added file') 'User collision bytes changed'
    Remove-Item -LiteralPath $collision
    Pass 'An added package filename cannot overwrite an existing user-added file'
    $lock=[IO.File]::Open((Join-Path $taskInstall 'zh-Hant/WindowsBase.resources.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{Check ((Setup $UpdatePackage 'failed-update') -ne 0) 'Update unexpectedly replaced a locked file'}finally{$lock.Dispose()}
    $null=Inventory;Check ((Get-FileHash -LiteralPath $saved).Hash -eq $modelHash) 'Failed update changed a user model'
    Check (!(Test-Path -LiteralPath $collision)) 'Failed update left a newly introduced payload file'
    Check ((Get-ItemProperty -LiteralPath $taskRegistry).DisplayVersion -eq '0.1.0') 'Failed update changed registered version'
    Pass 'A real locked-payload update fails and rolls installed bytes/version back; model and recovery remain'
    Check ((Setup $UpdatePackage 'update') -eq 0) 'Update failed'
    $manifest=Inventory;Check ($manifest.version -eq '0.1.1') 'Update payload version missing'
    Check ((Get-ItemProperty -LiteralPath $taskIdentityRegistry).Id -eq $installId) 'Update changed installation identity'
    Check ((Setup $UpdatePackage 'repair') -eq 0) 'Same-version repair failed'
    $null=Inventory
    Check ((Setup $BasePackage 'downgrade-refusal') -ne 0) 'Downgrade succeeded'
    $null=Inventory
    Pass 'Version0.1.0 →0.1.1 update and idempotent same-version repair preserve identity; downgrade is refused'
    $marker=Join-Path $taskInstall '.builder-install.ini';Remove-Item -LiteralPath $marker
    Check ((Setup $UpdatePackage 'marker-repair') -eq 0) 'Registered marker recovery failed'
    Check ((Get-Content $marker -Raw) -match [regex]::Escape($installId)) 'Marker repair changed identity'
    Pass 'A missing ownership marker repairs through the matching per-user registration without adopting a foreign folder'
    # Simulate a complete, interrupted transaction journal; never pretend this is a power-cut test.
    $backup=Join-Path $taskInstall '.builder-rollback';New-Item -ItemType Directory -Path (Join-Path $backup 'files')|Out-Null
    Set-Content (Join-Path $backup 'owner.ini') "[Rollback]`nProductId=7C2AC698-9A70-49C8-BF7D-A58F2CF1EB90`nReady=1"
    Set-Content (Join-Path $backup 'old-files.txt') @('Builder.Mcp.dll')
    Set-Content (Join-Path $backup 'incoming-files.txt') @('Builder.Mcp.dll','interrupted-new-file.txt')
    Copy-Item -LiteralPath (Join-Path $taskInstall 'Builder.Mcp.dll') -Destination (Join-Path $backup 'files/Builder.Mcp.dll')
    Add-Content (Join-Path $taskInstall 'Builder.Mcp.dll') 'simulated interrupted replacement'
    Set-Content (Join-Path $taskInstall 'interrupted-new-file.txt') 'new owned file from interrupted transaction'
    Check ((Setup $UpdatePackage 'interrupted-journal-repair') -eq 0) 'Interrupted transaction recovery failed'
    $null=Inventory
    Check (!(Test-Path -LiteralPath $backup) -and !(Test-Path (Join-Path $taskInstall 'interrupted-new-file.txt'))) 'Interrupted transaction debris retained'
    Pass 'A simulated interrupted transaction restores the old owned bytes, removes new owned files and completes repair'
    $null=StartClient -Recovery
    $candidates=Call 'builder_recovery_list'
    Check ($candidates.data.Count -gt 0) 'Update lost recovery candidates'
    $scene=Call 'builder_recovery_restore' @{candidate=$candidates.data[0];expectedRevision=0}
    Check ($scene.state.filePath -eq $saved -and $scene.state.isDirty -and !$scene.state.canUndo) 'Restore lost native path/history rules'
    $scene=Call 'builder_save' @{expectedRevision=$scene.state.revision}
    Copy-Item -LiteralPath $saved -Destination (Join-Path $taskData 'accepted.3mf')
    CloseClient
    $modelHash=(Get-FileHash -LiteralPath $saved).Hash
    $sentinel=Join-Path $taskData 'settings-and-backup.txt';Set-Content $sentinel 'retained user data'
    Check ((Uninstall 'uninstall') -eq 0) 'Uninstall failed'
    Check (!(Test-Path -LiteralPath (Join-Path $taskInstall 'Builder.exe'))) 'Uninstall left app binary'
    Check (!(Test-Path -LiteralPath $taskRegistry)) 'Uninstall registration left behind'
    Check (!(Test-Path -LiteralPath $taskIdentityRegistry)) 'Uninstall identity backup left behind'
    Check ((Get-FileHash -LiteralPath $saved).Hash -eq $modelHash) 'Uninstall deleted/changed user-added model'
    Check ((Get-Content $sentinel -Raw).Trim() -eq 'retained user data') 'Uninstall changed separate data'
    Pass 'Recovery survives update and restores saved native path; uninstall removes application/registration but keeps user-added model and separate data'
    Check ((Setup $BasePackage 'reinstall') -eq 0) 'Reinstall into owned retained folder failed'
    $null=Inventory
    Check ((Get-FileHash -LiteralPath $saved).Hash -eq $modelHash) 'Reinstall changed retained model'
    Check ((Uninstall 'final-uninstall') -eq 0) 'Final uninstall failed'
    foreach($taskName in $taskProfileBefore.Keys){Check ((Get-FileHash -LiteralPath (Join-Path $taskProfile $taskName)).Hash -eq $taskProfileBefore[$taskName]) 'Actual user profile changed'}
    Pass 'Reinstall recognizes retained owned user files; final uninstall and real-user preferences/recent-file preservation pass'
}catch{
    $taskEvents.Add(@{failure=$_.ToString()});throw
}finally{
    if($script:taskClient){CloseClient}
    @{utc=[DateTime]::UtcNow.ToString('o');passed=$taskPassed.ToArray();events=$taskEvents.ToArray();install=$taskInstall;runtime=$version;os=[Environment]::OSVersion.VersionString;globalDotnetPresent=[bool](Get-Command dotnet -ErrorAction SilentlyContinue);baseSha256=(Get-FileHash -LiteralPath $BasePackage).Hash;updateSha256=(Get-FileHash -LiteralPath $UpdatePackage).Hash}|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $taskEvidence 'results.json') -Encoding UTF8
}
