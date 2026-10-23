param([string]$PackageDirectory,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$releaseRoot=Split-Path $PSScriptRoot
$releaseVersion=(& (Join-Path $PSScriptRoot 'Test-ReleaseVersion.ps1')).Trim()
if(!$PackageDirectory){$PackageDirectory=Join-Path $releaseRoot ".local/packages/$releaseVersion"}
if(!$OutputDirectory){$OutputDirectory=Join-Path $releaseRoot ".local/release-assets/$releaseVersion"}
$releaseOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $releaseOutput){throw 'Choose a fresh release asset directory. Existing assets are never overwritten.'}
$releaseSetup=Join-Path $PackageDirectory "3D-BuilderCrap-$releaseVersion-win-x64-Setup.exe"
$payloadManifestPath=Join-Path $PackageDirectory 'payload/release-manifest.json'
$payloadManifest=Get-Content -LiteralPath $payloadManifestPath -Raw | ConvertFrom-Json
if($payloadManifest.version -cne $releaseVersion -or $payloadManifest.testChannel){throw 'Expected the matching production package, not a Packaging Test build.'}
$setupHash=(Get-FileHash -LiteralPath $releaseSetup -Algorithm SHA256).Hash
$recordedChecksum=(Get-Content -LiteralPath (Join-Path $PackageDirectory 'SHA256SUMS.txt') -Raw).Trim()
if($recordedChecksum -cne "$setupHash  $([IO.Path]::GetFileName($releaseSetup))"){throw 'Installer does not match its package checksum.'}
if((Get-Item -LiteralPath $releaseSetup).VersionInfo.ProductVersion.Trim() -cne $releaseVersion){throw 'Installer version metadata does not match the source release.'}
New-Item -ItemType Directory -Path $releaseOutput | Out-Null
Copy-Item -LiteralPath $releaseSetup -Destination $releaseOutput
Copy-Item -LiteralPath $payloadManifestPath -Destination (Join-Path $releaseOutput 'release-manifest.json')
Copy-Item -LiteralPath (Join-Path $releaseRoot 'LICENSE') -Destination (Join-Path $releaseOutput 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $releaseRoot "docs/releases/$releaseVersion.md") -Destination (Join-Path $releaseOutput 'RELEASE-NOTES.md')
$checksums=Get-ChildItem -LiteralPath $releaseOutput -File | Sort-Object Name | ForEach-Object {"$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $($_.Name)"}
$checksums | Set-Content -LiteralPath (Join-Path $releaseOutput 'SHA256SUMS.txt') -Encoding utf8
Write-Output "Release assets ready: $releaseOutput"
