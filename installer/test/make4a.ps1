# Real-size set: the 4A package as GitHub data parts, served beside its vault and the current installer.
param([string]$Package, [string]$Out, [string]$Tools)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\UcuPaths.ps1')
if (-not $Package) { $Package = Join-Path $UcuWorkspace 'transplant-gate\release-2.0.0-4A-package' }
if (-not $Out) { $Out = Join-Path $UcuWorkspace 'transplant-gate\release-gate-test\remote-4A-assets' }
if (Test-Path $Out) { throw "exists: $Out" }
[Reflection.Assembly]::LoadFrom("$Tools\BattleTechUCUSetup.exe") | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew()
$parts = [ReleaseGate.Volumes]::WriteParts($Package, $Out, '2.0.0-4A', [long](1.95 * 1GB), $null, [Threading.CancellationToken]::None)
Copy-Item "$Package\package.vault" $Out
Copy-Item "$Tools\BattleTechUCUSetup.exe" $Out
"parts written in $([int]$clock.Elapsed.TotalSeconds) s"
Get-ChildItem $Out | ForEach-Object { '{0} {1}' -f $_.Name, $_.Length }
