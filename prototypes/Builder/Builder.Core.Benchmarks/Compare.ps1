param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$reportRoot=(Resolve-Path -LiteralPath $EvidenceRoot).Path
$baselineReports=@('core-baseline.json','core-baseline-2.json','core-baseline-3.json')
$candidateReports=@('core-optimized.json','core-optimized-2.json','core-optimized-3.json')
function Read-Reports($names) {
    @($names | ForEach-Object { Get-Content -LiteralPath (Join-Path $reportRoot $_) -Raw | ConvertFrom-Json })
}
function Quantile($values,[double]$p) {
    $sorted=@($values | Sort-Object); $rank=$p*($sorted.Count-1); $low=[int][Math]::Floor($rank); $high=[Math]::Min($low+1,$sorted.Count-1)
    $sorted[$low]+($sorted[$high]-$sorted[$low])*($rank-$low)
}
function Summaries($reports) {
    @($reports.rows | Where-Object Index -ge 0 | Group-Object File,Stage | ForEach-Object {
        $rows=@($_.Group); $times=@($rows.Milliseconds); $allocations=@($rows.ManagedAllocatedBytes)
        [pscustomobject]@{file=$rows[0].File;stage=$rows[0].Stage;n=$rows.Count;min=($times | Measure-Object -Minimum).Minimum;
            median=(Quantile $times .5);p95=(Quantile $times .95);max=($times | Measure-Object -Maximum).Maximum;
            medianAllocatedBytes=(Quantile $allocations .5)}
    })
}
$baseline=Read-Reports $baselineReports; $candidate=Read-Reports $candidateReports
$all=@($baseline)+@($candidate)
if(@($all.tools.sourceSha256 | Select-Object -Unique).Count -ne 1) {throw 'Reports were produced by different benchmark source.'}
if(@($baseline.target.sha256 | Select-Object -Unique).Count -ne 1 -or @($candidate.target.sha256 | Select-Object -Unique).Count -ne 1) {throw 'A report set mixes Core binaries.'}
if($baseline[0].target.sha256 -eq $candidate[0].target.sha256) {throw 'Baseline and candidate binaries are identical.'}
foreach($inputFile in $baseline[0].inputs.file) {
    if(@($all.inputs | Where-Object file -eq $inputFile | Select-Object -ExpandProperty sha256 -Unique).Count -ne 1) {throw "Input changed: $inputFile"}
}
foreach($report in $all) {if($report.history.Count -ne 15) {throw 'Expected five history cycles for each of three inputs.'}}
$before=Summaries $baseline; $after=Summaries $candidate
$comparisons=@($after | ForEach-Object {
    $current=$_; $prior=$before | Where-Object { $_.file -eq $current.file -and $_.stage -eq $current.stage }
    [pscustomobject]@{file=$current.file;stage=$current.stage;baseline=$prior;candidate=$current;
        medianSpeedRatio=if($prior -and $current.median -gt 0){$prior.median/$current.median}else{$null}}
})
$reportHashes=@(@($baselineReports)+@($candidateReports) | ForEach-Object {@{file=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $reportRoot $_) -Algorithm SHA256).Hash}})
$result=@{timestamp=[DateTimeOffset]::UtcNow;method='Three fresh processes per binary; combined measured rows after three warmups. Same controlled input hashes/source; five bounded 100-step history cycles.';
    comparisonTool=@{powershell=$PSVersionTable.PSVersion.ToString();sourceSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash};
    baselineSha256=$baseline[0].target.sha256;candidateSha256=$candidate[0].target.sha256;benchmarkSourceSha256=$all[0].tools.sourceSha256;
    reports=$reportHashes;comparisons=$comparisons;
    cancellations=@{baselineMaximumMilliseconds=($baseline.cancellations.signalToCompletionMilliseconds | Measure-Object -Maximum).Maximum;
        candidateMaximumMilliseconds=($candidate.cancellations.signalToCompletionMilliseconds | Measure-Object -Maximum).Maximum};
    history=@{baseline=@($baseline | ForEach-Object { @{started=$_.started;cycles=$_.history} });candidate=@($candidate | ForEach-Object { @{started=$_.started;cycles=$_.history} })};
    limitations=@('Process-cold first pick is only the first fixture in each process; later fixtures are operation-cold in a warmed runtime.',
        'Managed live history totals include retained benchmark measurement records, grow slightly per cycle, and exclude native/peak memory.',
        'No baseline Core picking API existed. Brute ray timings are a harness reference, not baseline product picking.',
        'Three fixed warmups cannot guarantee fully settled tiered JIT. Raw rows and cold-first measurements remain in each report.')}
$result | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath (Join-Path $reportRoot 'core-comparison.json') -Encoding utf8
$comparisons | Where-Object stage -in @('move-preview','rotate-preview','scale-preview','core-pick-first','core-pick-hit','core-pick-miss','selection-units') |
    Select-Object file,stage,@{n='baselineMedian';e={$_.baseline.median}},@{n='candidateMedian';e={$_.candidate.median}},@{n='candidateP95';e={$_.candidate.p95}} | Format-Table
