param([string]$Tag)
$ErrorActionPreference='Stop'
$releaseRoot=Split-Path $PSScriptRoot
$sourceRoot=Join-Path $releaseRoot 'src/BuilderCrap'
[xml]$releaseProperties=Get-Content -LiteralPath (Join-Path $sourceRoot 'Directory.Build.props') -Raw
$releaseVersion=[string]$releaseProperties.Project.PropertyGroup.Version
if($releaseVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Directory.Build.props must contain a numeric major.minor.patch version.'}
[xml]$releaseManifest=Get-Content -LiteralPath (Join-Path $sourceRoot 'Builder.Desktop/app.manifest') -Raw
if($releaseManifest.assembly.assemblyIdentity.version -cne "$releaseVersion.0"){throw 'Desktop manifest version must match Directory.Build.props.'}
$installerSource=Get-Content -LiteralPath (Join-Path $sourceRoot 'Packaging/Builder.iss') -Raw
$installerVersion=[regex]::Match($installerSource,'#define PackageVersion "(?<version>\d+\.\d+\.\d+)"').Groups['version'].Value
if($installerVersion -cne $releaseVersion){throw 'Installer fallback version must match Directory.Build.props.'}
if($Tag -and $Tag -cne "v$releaseVersion"){throw "Release tag must be v$releaseVersion; received $Tag."}
if(!(Test-Path -LiteralPath (Join-Path $releaseRoot "docs/releases/$releaseVersion.md"))){throw "Add docs/releases/$releaseVersion.md before preparing a release."}
Write-Output $releaseVersion
