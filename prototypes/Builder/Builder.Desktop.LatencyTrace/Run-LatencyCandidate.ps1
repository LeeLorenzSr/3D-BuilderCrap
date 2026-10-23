param(
    [Parameter(Mandatory=$true)][string]$BinaryDirectory,
    [string]$EvidenceRoot='.local/evidence/latency-trace-20261019',
    [switch]$IncludeTrace
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$evidence=[IO.Path]::GetFullPath((Join-Path $root $EvidenceRoot))
$binary=(Resolve-Path -LiteralPath $BinaryDirectory).Path
$candidate=Join-Path $evidence 'candidate'
$roles=@('canonical','counters');if($IncludeTrace){$roles+=@('traced')}
foreach($role in $roles){foreach($run in 1..3){
    $report=Join-Path $candidate ($role+'/run-'+$run.ToString('00')+'.json')
    if(Test-Path -LiteralPath $report){throw "Refusing to overwrite $report"}
}}
$desktop=(Get-FileHash -LiteralPath (Join-Path $binary 'Builder.dll') -Algorithm SHA256).Hash
$core=(Get-FileHash -LiteralPath (Join-Path $binary 'Builder.Core.dll') -Algorithm SHA256).Hash
if($desktop -eq '154F83F6F275AA3CC6051794F1E0FC3AE42C9965DDA497E1A1464DE6DE4CBD94'){throw 'Candidate Desktop is unchanged from154F baseline.'}
if($core -ne '941FBC0759107880BAD2E0CD95BF21A871315917A86C590BBB3DFF0B84461EF5'){throw 'Core changed; this paired profile requires the frozen Core.'}
$canonicalSource=Join-Path $root 'prototypes/Builder/Builder.Desktop.Performance/ExpandedGroupProgram.cs'
if((Get-FileHash -LiteralPath $canonicalSource -Algorithm SHA256).Hash -ne 'A15D5565911CE9A80DA7DC914203FC723D6774F23710F7E27D10364ECD2D6D8C'){throw 'Original canonical caller changed.'}
$counterBaseline=Get-Content -LiteralPath (Join-Path $evidence 'counters/run-01.json') -Raw | ConvertFrom-Json
foreach($entry in @(@{file='LatencyTraceProgram.cs';sha=$counterBaseline.harness.sourceSha256},@{file='TracePhases.cs';sha=$counterBaseline.trace.phaseSourceSha256},@{file='Builder.Desktop.LatencyTrace.csproj';sha=$counterBaseline.harness.projectSha256})){
    if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $entry.file) -Algorithm SHA256).Hash -ne $entry.sha){throw "Counter caller differs: $($entry.file)"}
}
$canonicalOut=Join-Path $candidate 'harness-canonical';$counterOut=Join-Path $candidate 'harness-counters'
& dotnet build (Join-Path $root 'prototypes/Builder/Builder.Desktop.Performance/Builder.Desktop.ExpandedGroupPerformance.csproj') -c Release "-p:BuilderBinaryDirectory=$binary" -o $canonicalOut
if($LASTEXITCODE -ne 0){throw 'Canonical caller build failed.'}
& dotnet build (Join-Path $PSScriptRoot 'Builder.Desktop.LatencyTrace.csproj') -c Release "-p:BuilderBinaryDirectory=$binary" -o $counterOut
if($LASTEXITCODE -ne 0){throw 'Counter caller build failed.'}
$inputPath=Join-Path $root '.local/evidence/performance-20261019/inputs/combined-1000-100000.3mf'
$commands=[Collections.Generic.List[object]]::new()
foreach($role in $roles){
    New-Item -ItemType Directory -Path (Join-Path $candidate $role) -Force | Out-Null
    foreach($run in 1..3){
        $stem=Join-Path $candidate ($role+'/run-'+$run.ToString('00'))
        $caller=if($role -eq 'canonical'){Join-Path $canonicalOut 'Builder.Desktop.ExpandedGroupPerformance.dll'}else{Join-Path $counterOut 'Builder.Desktop.LatencyTrace.dll'}
        if($role -eq 'traced'){
            $executable=Join-Path $root '.local/tools/dotnet-trace/dotnet-trace.exe'
            $arguments=@('collect','--profile','dotnet-common,dotnet-sampled-thread-time','--providers','Builder-Latency-Phases:0xffff:4','--buffersize','256','--output',($stem+'.nettrace'),'--show-child-io','--','dotnet',$caller,$inputPath,($stem+'.json'))
        }else{$executable='dotnet';$arguments=@($caller,$inputPath,($stem+'.json'))}
        $commands.Add(@{role=$role;run=$run;command=@($executable)+$arguments})
        & $executable @arguments
        if($LASTEXITCODE -ne 0){throw "Candidate $role run$run failed."}
    }
}
@{utc=[DateTime]::UtcNow.ToString('O');desktopSha256=$desktop;coreSha256=$core;commands=$commands} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $candidate 'run-commands.json') -Encoding utf8
