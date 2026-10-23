param([string]$EvidenceRoot='.local/evidence/latency-trace-20261019')
$ErrorActionPreference='Stop'
$source=(Resolve-Path -LiteralPath $EvidenceRoot).Path
$fixture=Join-Path $source 'tool-tests/comparison-fixture'
if(Test-Path -LiteralPath $fixture){throw 'Synthetic comparison fixture already exists; preserve it and use a fresh test root.'}
foreach($profile in @('canonical','counters')){foreach($run in 1..3){
    $filename='run-'+$run.ToString('00')+'.json'
    $from=Join-Path $source ($profile+'/'+$filename)
    $baseline=Join-Path $fixture ($profile+'/'+$filename)
    $candidate=Join-Path $fixture ('candidate/'+$profile+'/'+$filename)
    New-Item -ItemType Directory -Path (Split-Path $baseline),(Split-Path $candidate) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $baseline
    $fake=Get-Content -LiteralPath $from -Raw|ConvertFrom-Json
    # Synthetic report mutation tests the comparer, never an application binary/performance result.
    $fake.desktop.sha256='FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF'
    $fake|ConvertTo-Json -Depth 40|Set-Content -LiteralPath $candidate -Encoding utf8
}}
$comparer=Join-Path $PSScriptRoot 'Compare-Latency.ps1'
& $comparer -EvidenceRoot $fixture | Out-Null
$output=Get-Content -LiteralPath (Join-Path $fixture 'latency-comparison.json') -Raw|ConvertFrom-Json
$done=@($output.gates|Where-Object stage -eq warm.done-layout)[0]
if($done.passed -or $done.pooledP95Milliseconds -le 50 -or $done.worstProcessP95Milliseconds -gt 50){throw 'Synthetic same-distribution test did not exercise pooled-tail-only failure.'}
if($output.gates.Count -ne 5 -or $output.diagnosticComparisons.Count -ne 5 -or $output.allPerformanceGatesPass){throw 'Canonical/diagnostic gate separation failed.'}
$path=Join-Path $fixture 'candidate/canonical/run-01.json';$original=[IO.File]::ReadAllBytes($path)
$checks=[Collections.Generic.List[object]]::new()
foreach($case in @('missing-row','missing-sample','wrong-runtime')){
    try{
        $fake=[Text.Encoding]::UTF8.GetString($original).TrimStart([char]0xfeff)|ConvertFrom-Json
        if($case -eq 'missing-row'){$fake.stages=@($fake.stages|Where-Object stage -ne initial.expanded-rows)}
        elseif($case -eq 'missing-sample'){($fake.stages|Where-Object stage -eq warm.done-layout).samples=@(($fake.stages|Where-Object stage -eq warm.done-layout).samples|Select-Object -Skip 1)}
        else{$fake.tools.runtime='synthetic-wrong-runtime'}
        $fake|ConvertTo-Json -Depth 40|Set-Content -LiteralPath $path -Encoding utf8
        $rejected=$false;$message=''
        try{& $comparer -EvidenceRoot $fixture -OutputFile ($case+'.json') | Out-Null}catch{$rejected=$true;$message=$_.Exception.Message}
        if(!$rejected){throw "Comparer accepted $case"}
        $checks.Add(@{case=$case;rejected=$true;message=$message})
    }finally{[IO.File]::WriteAllBytes($path,$original)}
}
$rejected=$false
try{& $comparer -EvidenceRoot $fixture | Out-Null}catch{$rejected=$_.Exception.Message -like 'Refusing to overwrite*'}
if(!$rejected){throw 'Existing comparison was not rejected.'}
@{syntheticToolTest=$true;notApplicationEvidence=$true;passed=$true;pooledTailFailure=$done;canonicalGateCount=5;diagnosticComparisonCount=5;malformedReports=$checks;existingComparisonRejected=$true;toolSha256=(Get-FileHash -LiteralPath $comparer -Algorithm SHA256).Hash} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $source 'tool-tests/result.json') -Encoding utf8
Write-Output 'Synthetic comparison validation PASS (not application timing evidence).'
