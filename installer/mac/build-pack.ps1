# Builds the macOS-capable ReleasePack.exe into -Out: the Windows packer with UCU_MAC defined, so it also packs a macOS player
# (vault BTU6GAM4, package-mac.vault). Same pinned SDK and flags as ../build.ps1, so the same commit builds the same bytes. The
# ordinary ReleasePack.exe, BattleTechUCUSetup.exe and the other tools are built by ../build.ps1 with no macOS code in them.
#   -Out <folder>   where ReleasePack.exe goes (not under tools/release-gate: build.ps1's bin is the Windows tools')
param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sdkVersion = '10.0.404'   # keep equal to build.ps1
$sdk = (& dotnet --list-sdks | Where-Object { $_ -like "$sdkVersion *" } | Select-Object -First 1) -replace '^(\S+) \[(.*)\]$', '$2\$1'
if (-not $sdk) { throw ".NET SDK $sdkVersion is needed (dotnet --list-sdks): install that exact version." }
$csc = Join-Path $sdk 'Roslyn\bincore\csc.dll'
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$src = Join-Path $root 'src'
New-Item -ItemType Directory -Force $Out | Out-Null
$common = @('-nologo', '-noconfig', '-nostdlib+', '-platform:x64', '-optimize+', '-langversion:7.3', '-deterministic+', '-debug-', "-pathmap:$root=installer")
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Xml' | ForEach-Object { "-r:$fx\$_.dll" }
& dotnet $csc @common -target:exe "-resource:$src\gog-accept.tsv,gog-accept.tsv" "-out:$Out\ReleasePack.exe" '-define:PACK;UCU_MAC' @refs "$src\AssemblyInfo.cs" "$src\GateCore.cs" "$src\PublicManifest.cs" "$src\RetailRanges.cs" "$src\Pack.cs" "$src\MacLayout.cs"
if ($LASTEXITCODE -ne 0) { throw 'csc failed for ReleasePack (macOS)' }
Get-ChildItem $Out -Filter ReleasePack.exe | ForEach-Object { "$($_.Name) $($_.Length)" }
