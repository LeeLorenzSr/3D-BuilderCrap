param(
    [Parameter(Mandatory=$true)][string]$BinaryDirectory,
    [Parameter(Mandatory=$true)][ValidateSet('baseline','candidate')][string]$Role,
    [string]$EvidenceRoot,
    [string]$InputPath
)
$ErrorActionPreference='Stop'
$repositoryRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
if(!$EvidenceRoot){$EvidenceRoot=Join-Path $repositoryRoot '.local/evidence/expanded-groups-20261019'}
if(!$InputPath){$InputPath=Join-Path $repositoryRoot '.local/evidence/performance-20261019/inputs/combined-1000-100000.3mf'}
$binaryRoot=(Resolve-Path -LiteralPath $BinaryDirectory).Path
$inputFile=(Resolve-Path -LiteralPath $InputPath).Path
$evidenceDirectory=[IO.Path]::GetFullPath($EvidenceRoot)
$harnessDirectory=Join-Path $evidenceDirectory "harness-$Role"
$reportDirectory=Join-Path $evidenceDirectory $Role
foreach($run in 1..3){
    $existing=Join-Path $reportDirectory ('scope-'+$run.ToString('00')+'.json')
    if(Test-Path -LiteralPath $existing){throw "Refusing to overwrite completed evidence: $existing. Use a new EvidenceRoot for a later candidate."}
}
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
dotnet build (Join-Path $PSScriptRoot 'Builder.Desktop.ExpandedGroupPerformance.csproj') -c Release "-p:BuilderBinaryDirectory=$binaryRoot" -o $harnessDirectory
if($LASTEXITCODE -ne 0){throw 'Expanded-group caller build failed.'}
foreach($run in 1..3){
    $report=Join-Path $reportDirectory ('scope-'+$run.ToString('00')+'.json')
    dotnet (Join-Path $harnessDirectory 'Builder.Desktop.ExpandedGroupPerformance.dll') $inputFile $report
    if($LASTEXITCODE -ne 0){throw "Expanded-group process $run failed; inspect $report"}
}
