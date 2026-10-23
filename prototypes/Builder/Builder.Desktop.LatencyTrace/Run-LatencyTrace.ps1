param([string]$EvidenceRoot='.local/evidence/latency-trace-20261019')
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$evidence=[IO.Path]::GetFullPath((Join-Path $root $EvidenceRoot))
$inputPath=Join-Path $root '.local/evidence/performance-20261019/inputs/combined-1000-100000.3mf'
$canonical=Join-Path $root '.local/evidence/expanded-groups-20261019/final-measurement/harness-candidate/Builder.Desktop.ExpandedGroupPerformance.dll'
$caller=Join-Path $evidence 'harness/Builder.Desktop.LatencyTrace.dll'
$trace=Join-Path $root '.local/tools/dotnet-trace/dotnet-trace.exe'
foreach($role in @('canonical','counters','traced')){foreach($run in 1..3){
    $file=Join-Path $evidence ($role+'/run-'+$run.ToString('00')+'.json')
    if(Test-Path -LiteralPath $file){throw "Refusing to overwrite $file"}
}}
$commands=[Collections.Generic.List[object]]::new()
foreach($role in @('canonical','counters')){
    New-Item -ItemType Directory -Path (Join-Path $evidence $role) -Force | Out-Null
    foreach($run in 1..3){
        $output=Join-Path $evidence ($role+'/run-'+$run.ToString('00')+'.json')
        $executable=if($role -eq 'canonical'){$canonical}else{$caller}
        $commands.Add(@{role=$role;run=$run;command=@('dotnet',$executable,$inputPath,$output)})
        & dotnet $executable $inputPath $output
        if($LASTEXITCODE -ne 0){throw "$role run$run failed"}
    }
}
New-Item -ItemType Directory -Path (Join-Path $evidence 'traced') -Force | Out-Null
$wprOwned=$false
$status=(& wpr -status 2>&1 | Out-String)
$status | Set-Content (Join-Path $evidence 'wpr-status-before.txt')
if($status -match 'WPR is not recording'){
    $wprStart=(& wpr -start CPU -filemode 2>&1 | Out-String)
    $wprOwned=$LASTEXITCODE -eq 0
    $wprStart | Set-Content (Join-Path $evidence 'wpr-start.txt')
}else{ 'Existing WPR session left untouched.' | Set-Content (Join-Path $evidence 'wpr-start.txt') }
try{
    foreach($run in 1..3){
        $stem=Join-Path $evidence ('traced/run-'+$run.ToString('00'))
        $argsTrace=@('collect','--profile','dotnet-common,dotnet-sampled-thread-time','--providers','Builder-Latency-Phases:0xffff:4','--buffersize','256','--output',($stem+'.nettrace'),'--show-child-io','--','dotnet',$caller,$inputPath,($stem+'.json'))
        $commands.Add(@{role='traced';run=$run;command=@($trace)+$argsTrace})
        & $trace @argsTrace
        if($LASTEXITCODE -ne 0){throw "traced run$run failed"}
    }
}finally{
    if($wprOwned){& wpr -stop (Join-Path $evidence 'traced/native-cpu.etl') 2>&1 | Set-Content (Join-Path $evidence 'wpr-stop.txt')}
    @{utc=[DateTime]::UtcNow.ToString('O');wprOwned=$wprOwned;commands=$commands} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $evidence 'run-commands.json') -Encoding utf8
}
