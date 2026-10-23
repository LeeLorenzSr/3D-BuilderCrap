param([string]$EvidenceRoot='.local/evidence/latency-trace-20261019',[string]$OutputFile='latency-comparison.json')
$ErrorActionPreference='Stop'
$evidence=(Resolve-Path -LiteralPath $EvidenceRoot).Path
$output=Join-Path $evidence $OutputFile
if(Test-Path -LiteralPath $output){throw "Refusing to overwrite $output. Preserve the prior result and choose a new OutputFile."}
function Stats($values){
    $s=@($values|Sort-Object);if($s.Count -eq 0){throw 'Missing required samples.'}
    function Q([double]$p){$r=$p*($s.Count-1);$i=[int][Math]::Floor($r);$s[$i]+($s[[Math]::Min($i+1,$s.Count-1)]-$s[$i])*($r-$i)}
    @{count=$s.Count;minimum=$s[0];median=(Q .5);p95=(Q .95);maximum=$s[-1];over50=@($s|Where-Object {$_ -gt 50}).Count}
}
function Metric($reports,[string]$name,[string]$part='all'){
    $samples=@();$perProcess=@()
    foreach($report in $reports){
        $stage=@($report.stages|Where-Object stage -eq $name);if($stage.Count -ne 1){throw "Expected one stage $name"}
        $rows=@($stage[0].samples)
        if($part -eq 'warm'){$rows=@($rows|Select-Object -Skip 1)}elseif($part -eq 'first'){$rows=@($rows|Select-Object -First 1)}
        $samples+=@($rows);$perProcess+=@((Stats @($rows.Milliseconds)).p95)
    }
    @{milliseconds=(Stats @($samples.Milliseconds));perProcessP95=(Stats $perProcess);processAllocatedBytes=(Stats @($samples.ProcessAllocatedBytes));uiAllocatedBytes=(Stats @($samples.UiThreadAllocatedBytes))}
}
function DoneChronology($reports){
    @(for($run=0;$run -lt $reports.Count;$run++){
        $r=$reports[$run]
        foreach($sample in @($r.trace.samples|Where-Object stage -eq Done)){
            @{run=$run+1;processId=$r.trace.ProcessId;uiThreadId=$r.trace.UiNativeThreadId;sequence=$sample.sequence;wallMilliseconds=$sample.metrics.wallMilliseconds;gen0=$sample.metrics.gen0;gcPauseMilliseconds=$sample.metrics.gcPauseMilliseconds}
        }
    })
}
$profiles=@();$hashes=@();$gates=@();$diagnosticComparisons=@();$candidateDesktop=$null
foreach($profile in @('canonical','counters')){
    $sets=@{}
    foreach($version in @('baseline','candidate')){
        $relative=if($version -eq 'baseline'){$profile}else{'candidate/'+$profile}
        $reports=@(foreach($i in 1..3){
            $path=Join-Path $evidence ($relative+'/run-'+$i.ToString('00')+'.json')
            $hashes+=@(@{file=[IO.Path]::GetRelativePath($evidence,$path).Replace('\','/');sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash})
            Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        })
        if(@($reports.desktop.sha256|Select-Object -Unique).Count -ne 1){throw "$profile $version mixes Desktop versions."}
        if($version -eq 'baseline' -and $reports[0].desktop.sha256 -ne '154F83F6F275AA3CC6051794F1E0FC3AE42C9965DDA497E1A1464DE6DE4CBD94'){throw 'Baseline must be frozen154F.'}
        if($version -eq 'candidate'){
            if(!$candidateDesktop){$candidateDesktop=$reports[0].desktop.sha256}
            if($reports[0].desktop.sha256 -ne $candidateDesktop -or $candidateDesktop -eq '154F83F6F275AA3CC6051794F1E0FC3AE42C9965DDA497E1A1464DE6DE4CBD94'){throw 'Candidate profile versions differ or equal baseline.'}
        }
        foreach($r in $reports){
            if($r.failures.Count -ne 0 -or @($r.behavior|Where-Object {-not $_.passed}).Count -ne 0){throw "$version $profile failed behavior."}
            if($r.core.sha256 -ne '941FBC0759107880BAD2E0CD95BF21A871315917A86C590BBB3DFF0B84461EF5'){throw 'Core differs from frozen baseline.'}
            $expected=if($profile -eq 'canonical'){26}else{242}
            if($r.behavior.Count -ne $expected){throw 'Wrong fixed transition count.'}
            $rows=@($r.stages|Where-Object {$_.PSObject.Properties.Name -contains 'logicalSceneNodes'})
            $expectedRows=@('initial.expanded-rows','expanded.last-member-rows','member.initial-rows','member.after-undo-rows','done.expanded-rows','done.final-expanded-rows','done.last-member-rows')
            if($rows.Count -ne 7 -or @($expectedRows|Where-Object {$name=$_;@($rows|Where-Object stage -eq $name).Count -ne 1}).Count){throw 'Missing or duplicate required row snapshots.'}
            if(@($rows|Where-Object {$_.logicalSceneNodes -ne 1001 -or $_.actualListHeight -le 0 -or $_.actualListWidth -le 0 -or $_.realizedRowContainers -gt 80 -or $_.visualNodes -gt 2000}).Count){throw 'Logical scene/list bounds/realization contract failed.'}
            $expectedCounts=@{'cold.window-construct'=1;'cold.empty-layout-setup'=1;'cold.core-open'=1;'cold.expanded-initial-refresh-layout'=1;'warm.expanded-refresh-layout'=12;'cold.expanded-scroll-last-layout'=1;'warm.expanded-scroll-last-layout'=12;'expanded.last-member-selection-layout'=1;'cold.member-enter-layout'=1;'member.preview-layout-sequence'=10;'member.apply-layout-sequence'=10;'member.undo-layout-sequence'=10;'cold.done-layout'=1;'warm.member-enter-layout'=$(if($profile -eq 'canonical'){12}else{120});'warm.done-layout'=$(if($profile -eq 'canonical'){12}else{120})}
            $sampleStages=@($r.stages|Where-Object {$_.PSObject.Properties.Name -contains 'samples'})
            if($sampleStages.Count -ne $expectedCounts.Count){throw 'Wrong fixed measured stage count.'}
            foreach($name in $expectedCounts.Keys){$matches=@($sampleStages|Where-Object stage -eq $name);if($matches.Count -ne 1 -or $matches[0].samples.Count -ne $expectedCounts[$name]){throw "Wrong fixed sample count: $profile $version $name"}}
            if($profile -eq 'counters' -and ($r.trace.samples.Count -ne 243 -or @($r.trace.samples|Where-Object stage -eq Initial).Count -ne 1 -or @($r.trace.samples|Where-Object stage -eq Enter).Count -ne 121 -or @($r.trace.samples|Where-Object stage -eq Done).Count -ne 121)){throw 'Wrong fixed phase-sample count.'}
        }
        $sets[$version]=$reports
    }
    $all=@($sets.baseline)+@($sets.candidate)
    foreach($field in @('sourceSha256','projectSha256')){if(@($all.harness.$field|Select-Object -Unique).Count -ne 1){throw "$profile caller$field differs."}}
    if(@($all.input.sha256|Select-Object -Unique).Count -ne 1 -or @($all.configuration|Select-Object -Unique).Count -ne 1){throw "$profile input/config differs."}
    if(@($all.tools|ForEach-Object {$_|ConvertTo-Json -Compress}|Select-Object -Unique).Count -ne 1){throw "$profile runtime/OS/processor/GC configuration differs."}
    if($all[0].input.sha256 -ne '4DA0979CBC65944FD7EF556B9B6CBAD1315D13BBEC537C8B53BFD34D28120B72'){throw 'Input differs from authoritative combined limit fixture.'}
    if($profile -eq 'canonical' -and $all[0].harness.sourceSha256 -ne 'A15D5565911CE9A80DA7DC914203FC723D6774F23710F7E27D10364ECD2D6D8C'){throw 'Original canonical source changed.'}
    if($profile -eq 'counters' -and @($all.trace.phaseSourceSha256|Select-Object -Unique).Count -ne 1){throw 'Counter phase source differs.'}
    $metrics=@()
    foreach($name in @('cold.expanded-initial-refresh-layout','cold.member-enter-layout','cold.done-layout','warm.member-enter-layout','warm.done-layout','warm.expanded-scroll-last-layout','expanded.last-member-selection-layout','member.preview-layout-sequence','member.apply-layout-sequence','member.undo-layout-sequence')){
        $part=if($name -like '*-sequence'){'warm'}else{'all'}
        $baseline=Metric $sets.baseline $name $part;$candidate=Metric $sets.candidate $name $part
        $metrics+=@(@{stage=$name;part=$part;baseline=$baseline;candidate=$candidate})
        if($name -in @('cold.expanded-initial-refresh-layout','cold.done-layout','warm.done-layout','member.apply-layout-sequence','member.undo-layout-sequence')){
            $cold=$name -like 'cold.*'
            $passed=if($cold){$candidate.milliseconds.maximum -le 50}else{$candidate.milliseconds.p95 -le 50 -and $candidate.perProcessP95.maximum -le 50}
            $comparison=@{profile=$profile;stage=$name;targetMilliseconds=50;rule=$(if($cold){'Maximum first-use sample'}else{'Both pooled p95 and worst-process p95'});passed=$passed;pooledP95Milliseconds=$candidate.milliseconds.p95;worstProcessP95Milliseconds=$candidate.perProcessP95.maximum;maximumMilliseconds=$candidate.milliseconds.maximum;over50=$candidate.milliseconds.over50;performanceGate=$profile -eq 'canonical'}
            if($profile -eq 'canonical'){$gates+=@($comparison)}else{$diagnosticComparisons+=@($comparison)}
        }
    }
    $gc=if($profile -eq 'counters'){@{baseline=(DoneChronology $sets.baseline);candidate=(DoneChronology $sets.candidate)}}else{$null}
    $profiles+=@(@{name=$profile;sourceSha256=$all[0].harness.sourceSha256;projectSha256=$all[0].harness.projectSha256;metrics=$metrics;doneGcChronology=$gc})
}
@{schemaVersion=1;utc=[DateTime]::UtcNow.ToString('O');baselineDesktopSha256='154F83F6F275AA3CC6051794F1E0FC3AE42C9965DDA497E1A1464DE6DE4CBD94';candidateDesktopSha256=$candidateDesktop;coreSha256='941FBC0759107880BAD2E0CD95BF21A871315917A86C590BBB3DFF0B84461EF5';profiles=$profiles;gates=$gates;diagnosticComparisons=$diagnosticComparisons;reports=$hashes;allPerformanceGatesPass=@($gates|Where-Object {-not $_.passed}).Count -eq 0;comparisonTool=@{sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;powershell=$PSVersionTable.PSVersion.ToString()};limitations=@('Isolated detached WPF CPU/dispatcher/layout, no physical input or presentation timing.','Only original canonical A15D profile defines performance gates. Counter profile includes observer work; its separate50ms comparison is diagnostic only.','Longer fixed loop can change JIT/allocation state. All raw samples and maxima/exceedance counts retained; no-trace candidate compared only with no-trace baseline.')} | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath $output -Encoding utf8
Write-Output $output
