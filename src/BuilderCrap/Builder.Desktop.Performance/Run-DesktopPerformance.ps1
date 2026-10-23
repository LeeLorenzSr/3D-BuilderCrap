param(
    [Parameter(Mandatory=$true)][string]$Harness,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [int]$Runs=3,
    [int]$Iterations=10,
    [switch]$ShowWindow
)
$ErrorActionPreference='Stop'
$workspaceRoot=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$harnessPath=(Resolve-Path -LiteralPath $Harness).Path
$destination=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($destination) | Out-Null
$inputs=Join-Path $workspaceRoot '.local/evidence/performance-20261019/inputs'
$fixtures=@('dense-100000.stl','objects-1000.3mf','combined-1000-100000.3mf')
$processes=@()
for($runIndex=1; $runIndex -le $Runs; $runIndex++) {
    foreach($fixtureName in $fixtures) {
        $fixturePath=Join-Path $inputs $fixtureName
        $resultPath=Join-Path $destination (([IO.Path]::GetFileNameWithoutExtension($fixtureName))+'-'+$runIndex.ToString('00')+'.json')
        if(Test-Path -LiteralPath $resultPath) { throw "Refusing to replace prior timing: $resultPath" }
        Write-Output "Starting isolated process: $fixtureName run $runIndex/$Runs"
        $elapsed=[Diagnostics.Stopwatch]::StartNew()
        if($ShowWindow) { & dotnet $harnessPath $fixturePath $resultPath $Iterations show }
        else { & dotnet $harnessPath $fixturePath $resultPath $Iterations }
        $exitCode=$LASTEXITCODE
        $elapsed.Stop()
        if($exitCode -ne 0) { throw "Harness failed with exit $exitCode : $resultPath" }
        $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if($result.failures.Count -ne 0) { throw "Harness assertions failed: $resultPath" }
        $processes+=@{fixture=$fixtureName;run=$runIndex;result=$resultPath;processWallMilliseconds=$elapsed.Elapsed.TotalMilliseconds;exitCode=$exitCode}
        @{utc=[DateTime]::UtcNow.ToString('o');method='Sequential fresh processes; no concurrent timing';runs=$Runs;iterations=$Iterations;showWindow=[bool]$ShowWindow;processes=$processes} |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'processes.json')
    }
}
