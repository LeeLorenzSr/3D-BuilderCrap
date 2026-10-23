param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$evidence=(Resolve-Path -LiteralPath $EvidenceRoot).Path
function Reports([string]$role){@((1..3)|ForEach-Object{Get-Content -LiteralPath (Join-Path $evidence ($role+'/scope-'+$_.ToString('00')+'.json')) -Raw | ConvertFrom-Json})}
function Stats($values){
    $sorted=@($values|Sort-Object)
    if($sorted.Count -eq 0){throw 'A required metric has no samples.'}
    function Q([double]$p){$rank=$p*($sorted.Count-1);$lo=[int][Math]::Floor($rank);$hi=[Math]::Min($lo+1,$sorted.Count-1);$sorted[$lo]+($sorted[$hi]-$sorted[$lo])*($rank-$lo)}
    @{count=$sorted.Count;minimum=$sorted[0];median=(Q .5);p95=(Q .95);maximum=$sorted[-1]}
}
function Metric($reports,[string]$stage,[string]$part='all'){
    $samples=@();$perProcess=@()
    foreach($report in $reports){
        $match=@($report.stages|Where-Object stage -eq $stage)
        if($match.Count -ne 1){throw "Expected one stage $stage"}
        $rows=@($match[0].samples)
        if($part -eq 'first'){$rows=@($rows|Select-Object -First 1)}
        elseif($part -eq 'warm'){$rows=@($rows|Select-Object -Skip 1)}
        $samples+=@($rows);$perProcess+=@(Stats @($rows.Milliseconds))
    }
    @{milliseconds=(Stats @($samples.Milliseconds));processAllocatedBytes=(Stats @($samples.ProcessAllocatedBytes));
        uiThreadAllocatedBytes=(Stats @($samples.UiThreadAllocatedBytes));perProcessP95=(Stats @($perProcess.p95))}
}
$baseline=Reports baseline;$candidate=Reports candidate;$all=@($baseline)+@($candidate)
if(@($all.harness.sourceSha256|Select-Object -Unique).Count -ne 1 -or @($all.harness.projectSha256|Select-Object -Unique).Count -ne 1){throw 'Caller source/project differs between measured processes.'}
if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'ExpandedGroupProgram.cs') -Algorithm SHA256).Hash -ne $all[0].harness.sourceSha256 -or
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Builder.Desktop.ExpandedGroupPerformance.csproj') -Algorithm SHA256).Hash -ne $all[0].harness.projectSha256){throw 'Current caller source/project no longer matches the measured source.'}
if(@($all.input.sha256|Select-Object -Unique).Count -ne 1 -or @($all.configuration|Select-Object -Unique).Count -ne 1){throw 'Input or configuration differs.'}
if(@($baseline.desktop.sha256|Select-Object -Unique).Count -ne 1 -or @($candidate.desktop.sha256|Select-Object -Unique).Count -ne 1){throw 'A version set mixes Desktop binaries.'}
if($baseline[0].desktop.sha256 -ne '403510FB2A43573100CC5BF7EC226BD9B6B74F8BADBA51D9171A796B83627D3A'){throw 'Baseline Desktop does not match the frozen target.'}
if($baseline[0].desktop.sha256 -eq $candidate[0].desktop.sha256){throw 'Candidate and baseline Desktop binaries are identical.'}
if(@($all.core.sha256|Select-Object -Unique).Count -ne 1 -or $all[0].core.sha256 -ne '941FBC0759107880BAD2E0CD95BF21A871315917A86C590BBB3DFF0B84461EF5'){throw 'Core changed during a Desktop-only slice.'}
foreach($report in $all){
    if($report.failures.Count -ne 0){throw 'A measured process had fatal functional failures.'}
    $rows=@($report.stages|Where-Object {$_.PSObject.Properties.Name -contains 'visualNodes'})
    if(@($rows|Where-Object {$_.logicalSceneNodes -ne 1001 -or $_.actualListWidth -le 0 -or $_.actualListHeight -le 0}).Count -ne 0){throw 'Measured rows are missing logical members or actual nonzero layout bounds.'}
    if($report.behavior.Count -ne 26){throw 'Expected thirteen Enter and Done behavior observations per process.'}
}
$metrics=@()
foreach($stage in @('cold.window-construct','cold.empty-layout-setup','cold.core-open','cold.expanded-initial-refresh-layout',
    'warm.expanded-refresh-layout','cold.expanded-scroll-last-layout','warm.expanded-scroll-last-layout','expanded.last-member-selection-layout',
    'cold.member-enter-layout','cold.done-layout','warm.member-enter-layout','warm.done-layout')){
    $metrics+=@{stage=$stage;baseline=(Metric $baseline $stage);candidate=(Metric $candidate $stage)}
}
foreach($stage in @('member.preview-layout-sequence','member.apply-layout-sequence','member.undo-layout-sequence')){
    $metrics+=@{stage=$stage;firstInteraction=@{baseline=(Metric $baseline $stage first);candidate=(Metric $candidate $stage first)};
        remainingWarm=@{baseline=(Metric $baseline $stage warm);candidate=(Metric $candidate $stage warm)}}
}
$gates=@{}
foreach($stage in @('cold.expanded-initial-refresh-layout','cold.done-layout','warm.done-layout','member.apply-layout-sequence','member.undo-layout-sequence')){
    $part=if($stage -like '*sequence'){'warm'}else{'all'}
    $measurement=Metric $candidate $stage $part
    $worst=if($stage -like 'cold.*'){$measurement.milliseconds.maximum}else{[Math]::Max($measurement.milliseconds.p95,$measurement.perProcessP95.maximum)}
    $gates[$stage]=@{targetMilliseconds=50;worstMeasuredMilliseconds=$worst;passed=$worst -le 50;part=$part;
        pooledP95Milliseconds=$measurement.milliseconds.p95;worstProcessP95Milliseconds=$measurement.perProcessP95.maximum;
        rule=if($stage -like 'cold.*'){'Maximum of first-use samples'}else{'Both pooled p95 and worst-process p95 must meet the same threshold'}}
}
$candidateBehaviorFailures=@($candidate.behavior|Where-Object {!$_.passed})
$gates['settled-transition-behavior']=@{passed=$candidateBehaviorFailures.Count -eq 0;failures=$candidateBehaviorFailures}
$candidateRows=@($candidate.stages|Where-Object {$_.PSObject.Properties.Name -contains 'visualNodes'})
$worstRealized=($candidateRows.realizedRowContainers|Measure-Object -Maximum).Maximum
$worstVisuals=($candidateRows.visualNodes|Measure-Object -Maximum).Maximum
$gates['bounded-realization']=@{passed=$worstRealized -le 80 -and $worstVisuals -le 2000;maximumRealizedContainers=$worstRealized;maximumVisualNodes=$worstVisuals;containerLimit=80;visualNodeLimit=2000}
$rowStages=@('initial.expanded-rows','expanded.last-member-rows','member.initial-rows','member.after-undo-rows','done.expanded-rows','done.final-expanded-rows','done.last-member-rows')
$rowComparisons=@($rowStages|ForEach-Object{$stage=$_;@{stage=$stage;baseline=@($baseline.stages|Where-Object stage -eq $stage);candidate=@($candidate.stages|Where-Object stage -eq $stage)}})
$reports=@(foreach($role in @('baseline','candidate')){foreach($run in 1..3){$relative=$role+'/scope-'+$run.ToString('00')+'.json';@{file=$relative;sha256=(Get-FileHash -LiteralPath (Join-Path $evidence $relative) -Algorithm SHA256).Hash}}})
$result=@{schemaVersion=1;utc=[DateTimeOffset]::UtcNow;sourceSha256=$all[0].harness.sourceSha256;projectSha256=$all[0].harness.projectSha256;
    baselineDesktopSha256=$baseline[0].desktop.sha256;candidateDesktopSha256=$candidate[0].desktop.sha256;coreSha256=$all[0].core.sha256;
    input=$all[0].input;tools=$all[0].tools;comparisonTool=@{powershell=$PSVersionTable.PSVersion.ToString();sourceSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash};
    method='Three fresh processes/version, identical caller/input/configuration. Root content explicitly detached from hidden Window; empty tabs/templates settle before Open. Every gated operation includes handler, actual1280x780 DIP measure/arrange/UpdateLayout, dispatcher Background drain and final layout. Scene remains expanded; both nested and flat row mappings accepted. First preview/Apply/Undo separate from nine remaining samples/process. Warm gates require both pooled p95 and worst-process p95; cold gates use the maximum first-use sample. Pre-tightening comparator/output snapshots are preserved separately; raw measurements are unchanged.';
    reports=$reports;metrics=$metrics;rows=$rowComparisons;gates=$gates;
    baselineBehaviorFailures=@($baseline.behavior|Where-Object {!$_.passed});candidateBehaviorFailures=$candidateBehaviorFailures;
    limitations=@('Detached WPF control/layout runtime evidence, not native mouse/keyboard, GPU frames or physical presentation latency.',
        'Detached content does not own native keyboard focus. Focus continuation needs the separate shown-window/native checks; queued layout/scroll work is included here.',
        'Prior ScopeProgram hosting and source are preserved; historical scope numbers are context, not directly paired with this revised caller.',
        'Allocation deltas include process/dispatcher runtime activity; no peak or native memory measurement.',
        'Single synthetic combined-limit input and one workstation. Cold first scene follows separately measured empty-window initialization; filesystem cache is not purged.')}
$result|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $evidence 'expanded-groups-comparison.json') -Encoding utf8
$gates|ConvertTo-Json -Depth 8
