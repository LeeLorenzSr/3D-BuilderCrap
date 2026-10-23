param(
    [string]$EvidenceDirectory='.local/evidence/performance-20261019'
)
$ErrorActionPreference='Stop'
$evidence=(Resolve-Path -LiteralPath $EvidenceDirectory).Path
function Read-Reports([string]$directory) {
    $reports=@(Get-ChildItem -LiteralPath (Join-Path $evidence $directory) -File | Where-Object Name -match '-0[123]\.json$' |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
    if($reports.Count -ne 9) {throw "Expected nine fresh processes in $directory; got $($reports.Count)"}
    foreach($report in $reports) {if($report.failures.Count -ne 0) {throw "Failed report in $directory"}}
    return $reports
}
function Stats($values) {
    $sorted=@($values | Sort-Object)
    if($sorted.Count -eq 0) {throw 'No metric samples'}
    return @{count=$sorted.Count;median=$sorted[[int][Math]::Floor($sorted.Count/2)];p95=$sorted[[Math]::Max(0,[int][Math]::Ceiling($sorted.Count*.95)-1)];minimum=$sorted[0];maximum=$sorted[-1]}
}
function Measure-Stage($reports,[string]$stage,[string]$part='all') {
    $elapsed=@(); $allocations=@(); $perProcessP95=@()
    foreach($report in $reports) {
        $row=@($report.stages | Where-Object stage -eq $stage)
        if($row.Count -ne 1) {throw "Missing/duplicate stage $stage"}
        $samples=@($row[0].samples)
        if($part -eq 'first') {$samples=@($samples | Select-Object -First 1)}
        elseif($part -eq 'remaining') {$samples=@($samples | Select-Object -Skip 1)}
        $operations=[Math]::Max(1,[int]$row[0].operationsPerSample)
        $processTimes=@($samples | ForEach-Object {$_.Milliseconds/$operations})
        $elapsed+=$processTimes
        $allocations+=@($samples | ForEach-Object {if($null -ne $_.ProcessAllocatedBytes) {$_.ProcessAllocatedBytes/$operations} else {$_.AllocatedBytes/$operations}})
        $perProcessP95+=(Stats $processTimes).p95
    }
    return @{milliseconds=Stats $elapsed;perProcessP95Range=@{minimum=($perProcessP95 | Measure-Object -Minimum).Minimum;maximum=($perProcessP95 | Measure-Object -Maximum).Maximum};
        processAllocatedBytes=Stats $allocations}
}
$baseline=Read-Reports 'desktop-baseline'; $optimized=Read-Reports 'desktop-optimized'
$sourceHashes=@(($baseline+$optimized).harness.sourceSha256 | Sort-Object -Unique)
if($sourceHashes.Count -ne 1) {throw 'Main harness source hashes differ'}
$baselineDesktop=@($baseline.desktop.sha256 | Sort-Object -Unique); $optimizedDesktop=@($optimized.desktop.sha256 | Sort-Object -Unique)
$baselineCore=@($baseline.core.sha256 | Sort-Object -Unique); $optimizedCore=@($optimized.core.sha256 | Sort-Object -Unique)
if($baselineDesktop.Count -ne 1 -or $optimizedDesktop.Count -ne 1 -or $baselineDesktop[0] -eq $optimizedDesktop[0]) {throw 'Expected distinct, internally stable Desktop binary hashes'}
if($baselineCore.Count -ne 1 -or $optimizedCore.Count -ne 1 -or $baselineCore[0] -eq $optimizedCore[0]) {throw 'Expected distinct, internally stable Core binary hashes'}
$fixtures=@()
foreach($inputPath in @($baseline.input.path | Sort-Object -Unique)) {
    $before=@($baseline | Where-Object {$_.input.path -eq $inputPath}); $after=@($optimized | Where-Object {$_.input.path -eq $inputPath})
    if($before.Count -ne 3 -or $after.Count -ne 3) {throw "Expected three paired processes: $inputPath"}
    if(@(($before+$after).input.sha256 | Sort-Object -Unique).Count -ne 1) {throw "Input changed: $inputPath"}
    if(@(($before+$after).configuration | Sort-Object -Unique).Count -ne 1) {throw 'Configuration mismatch'}
    $metrics=@()
    foreach($stage in @('cold.window.construct','cold.detached-layout','cold.core-open','cold.refresh-first-mesh','cold.software-render','cold.wpf-pick','cold.adapter-pick',
        'warm.selection-target','warm.scene-bounds','warm.refresh-fields','warm.render-scene','warm.camera-update','warm.layout','warm.wpf-pick.individual','warm.adapter-pick.individual','warm.wpf-pick.batch-average','warm.adapter-pick.batch-average','warm.software-render')) {
        $metrics+=@{stage=$stage;baseline=Measure-Stage $before $stage;optimized=Measure-Stage $after $stage}
    }
    foreach($stage in @('warm.desktop-preview-to-idle','warm.desktop-apply-refresh','warm.desktop-undo-refresh','warm.desktop-cancel-refresh')) {
        $metrics+=@{stage=$stage;interpretation='Sequence stage: first sample is first interaction; warm statistics exclude that sample';
            firstInteraction=@{baseline=Measure-Stage $before $stage 'first';optimized=Measure-Stage $after $stage 'first'};
            remainingWarm=@{baseline=Measure-Stage $before $stage 'remaining';optimized=Measure-Stage $after $stage 'remaining'}}
    }
    $memory=@()
    foreach($stage in @('memory.empty','memory.loaded','memory.after-preview-history','memory.after-reopen-gc')) {
        $memory+=@{stage=$stage;baselineManagedRetainedBytes=Stats @($before | ForEach-Object {($_.stages | Where-Object stage -eq $stage).memory.managedRetainedBytes});
            optimizedManagedRetainedBytes=Stats @($after | ForEach-Object {($_.stages | Where-Object stage -eq $stage).memory.managedRetainedBytes})}
    }
    $pick=Measure-Stage $after 'warm.adapter-pick.individual'; $coldPick=Measure-Stage $after 'cold.adapter-pick'
    $preview=Measure-Stage $after 'warm.desktop-preview-to-idle' 'remaining'; $undo=Measure-Stage $after 'warm.desktop-undo-refresh' 'remaining'
    $fixtures+=@{input=$before[0].input;processesPerVersion=3;iterationsPerProcess=$before[0].iterations;metrics=$metrics;memory=$memory;
        gates=@{warmIndividualPickP95=@{targetMilliseconds=16;worstProcessP95=$pick.perProcessP95Range.maximum;passed=$pick.perProcessP95Range.maximum -le 16};
            coldPick=@{targetMilliseconds=250;worstProcess=$coldPick.milliseconds.maximum;passed=$coldPick.milliseconds.maximum -le 250};
            warmDesktopPreviewP95=@{targetMilliseconds=50;worstProcessP95=$preview.perProcessP95Range.maximum;passed=$preview.perProcessP95Range.maximum -le 50};
            warmDesktopUndoP95=@{targetMilliseconds=50;worstProcessP95=$undo.perProcessP95Range.maximum;passed=$undo.perProcessP95Range.maximum -le 50}}}
}
$scopeProfiles=$null
if((Test-Path -LiteralPath (Join-Path $evidence 'scope-baseline')) -and (Test-Path -LiteralPath (Join-Path $evidence 'scope-optimized'))) {
    $scopeBefore=@(Get-ChildItem -LiteralPath (Join-Path $evidence 'scope-baseline') -Filter 'scope-0?.json' | ForEach-Object {Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json})
    $scopeAfter=@(Get-ChildItem -LiteralPath (Join-Path $evidence 'scope-optimized') -Filter 'scope-0?.json' | ForEach-Object {Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json})
    if($scopeBefore.Count -ne 3 -or $scopeAfter.Count -ne 3) {throw 'Scope profile requires three processes per version'}
    if(@(($scopeBefore+$scopeAfter).harness.sourceSha256 | Sort-Object -Unique).Count -ne 1 -or @(($scopeBefore+$scopeAfter).input.sha256 | Sort-Object -Unique).Count -ne 1) {throw 'Scope profile source/input mismatch'}
    foreach($report in $scopeBefore) {if($report.desktop.sha256 -ne $baselineDesktop[0] -or $report.core.sha256 -ne $baselineCore[0] -or $report.failures.Count -ne 0) {throw 'Scope baseline binary/assertion mismatch'}}
    foreach($report in $scopeAfter) {if($report.desktop.sha256 -ne $optimizedDesktop[0] -or $report.core.sha256 -ne $optimizedCore[0] -or $report.failures.Count -ne 0) {throw 'Scope optimized binary/assertion mismatch'}}
    $scopeMetrics=@()
    foreach($stage in @('whole.initial-refresh-layout','whole.refresh-layout','member.enter-refresh-layout','member.refresh-layout','scope.exit-refresh-layout')) {
        $scopeMetrics+=@{stage=$stage;baseline=Measure-Stage $scopeBefore $stage;optimized=Measure-Stage $scopeAfter $stage}
    }
    foreach($stage in @('member.preview-sequence','member.apply-sequence','member.undo-sequence')) {
        $scopeMetrics+=@{stage=$stage;firstInteraction=@{baseline=Measure-Stage $scopeBefore $stage 'first';optimized=Measure-Stage $scopeAfter $stage 'first'};
            remainingWarm=@{baseline=Measure-Stage $scopeBefore $stage 'remaining';optimized=Measure-Stage $scopeAfter $stage 'remaining'}}
    }
    $scopeRows=@()
    foreach($stage in @('whole.rows','member.rows','whole.restored-rows')) {
        $scopeRows+=@{stage=$stage;baseline=@($scopeBefore | ForEach-Object {$_.stages | Where-Object stage -eq $stage});optimized=@($scopeAfter | ForEach-Object {$_.stages | Where-Object stage -eq $stage})}
    }
    $scopeGates=@{}
    foreach($stage in @('whole.initial-refresh-layout','whole.refresh-layout','member.enter-refresh-layout','member.refresh-layout','member.apply-sequence','member.undo-sequence','scope.exit-refresh-layout')) {
        $part=if($stage -like '*sequence') {'remaining'} else {'all'}
        $metric=Measure-Stage $scopeAfter $stage $part
        $scopeGates[$stage]=@{targetMilliseconds=50;worstProcessP95=$metric.perProcessP95Range.maximum;passed=$metric.perProcessP95Range.maximum -le 50;
            interpretation='CPU handler plus explicitly forced WPF layout. First sequence interaction excluded for Apply/Undo; initial/entry/exit transitions are retained as their own cold/structural measurements. No physical presentation claim'}
    }
    $scopeProfiles=@{processesPerVersion=3;input=$scopeBefore[0].input;matchedHarnessSourceSha256=$scopeBefore[0].harness.sourceSha256;
        method='Separate identical-source profile for expanded whole group and independent member list. Refresh, Apply and Undo explicitly force WPF layout; preview waits for the operation without asserting a completed frame. CPU/software, not native input.';metrics=$scopeMetrics;rows=$scopeRows;gates=$scopeGates}
}
$result=@{schemaVersion=1;utc=[DateTime]::UtcNow.ToString('o');matchedHarnessSourceSha256=$sourceHashes[0];
    baseline=@{desktopSha256=$baselineDesktop[0];coreSha256=$baselineCore[0];directory='desktop-baseline'};
    optimized=@{desktopSha256=$optimizedDesktop[0];coreSha256=$optimizedCore[0];directory='desktop-optimized'};
    method='Identical source/configuration/input digests; three fresh processes per fixture/version, serial exclusive timing. Raw individual pick samples aggregated, with each process p95 range. Batch-average throughput reported separately. First transform interaction separate from remaining nine warm samples per process. Main Apply/Undo are synchronous handler plus Refresh CPU and exclude deferred layout. Preview waits for its operation to become idle, not for a completed frame. Software bitmap raster and explicit-layout scope profile are separate. No physical presentation latency claim.';
    limitations=@('Deterministic screen grid mixes hits, misses and edges; it is not an empirical distribution of user click locations.','Ten transform iterations measure limited desktop history retention; separate Core hundred-step/cycle evidence owns the 100-step plateau gate.','Software-render costs remain separate from adapter CPU gates.','Native input, OS dialogs and presentation latency require separate evidence.');fixtures=$fixtures;scopeProfiles=$scopeProfiles}
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $evidence 'desktop-comparison.json')
foreach($fixture in $fixtures) {Write-Output ([IO.Path]::GetFileName($fixture.input.path)); $fixture.gates | ConvertTo-Json -Depth 5}
