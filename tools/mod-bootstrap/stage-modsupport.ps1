<#
.SYNOPSIS
  Fills a ModSupport folder: the files that let ModTek run on UCU, kept inert until copied beside BattleTech.exe.
  A release player carries it as <player>\ModSupport (tools/release-gate Pack.Ships lets that folder through); tools/bta-link
  builds its volunteer zip from the same folder. Link BTA ships in it too; run from <install>\ModSupport it finds the install as its parent folder.

.PARAMETER Dest     The folder to fill, e.g. <player>\ModSupport. Created; must not exist.
.PARAMETER Managed  A player's BattleTech_Data\Managed (the bootstrap references its mscorlib only; the MissionControl
                    encounter record embedded in it references its Assembly-CSharp, UnityEngine and Newtonsoft).
.PARAMETER Bta      An install with Mods\ModTek (the bootstrap references ModTek's 0Harmony and Mono.Cecil; they are not copied).
.PARAMETER Winhttp  x64\winhttp.dll from doorstop_win_release_4.6.0.zip (README.md pins its SHA-256).
.PARAMETER DoorstopLicense  LICENSE from the same zip; defaults to the copy beside this script. Doorstop is LGPL 2.1: it ships with the DLL.
#>
param(
    [Parameter(Mandatory)][string]$Dest,
    [Parameter(Mandatory)][string]$Managed,
    [Parameter(Mandatory)][string]$Bta,
    [Parameter(Mandatory)][string]$Winhttp,
    [string]$DoorstopLicense = (Join-Path $PSScriptRoot 'DOORSTOP-LICENSE.txt')
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Dest) { throw "$Dest exists. Pick a new folder." }
$pin = '0F39399EBA05DEA9419781187AE2A6B0555AFCDF1859F1319B6213BE7729013B'
if ((Get-FileHash $Winhttp -Algorithm SHA256).Hash -ne $pin) { throw "$Winhttp is not Doorstop 4.6.0 x64 winhttp.dll." }

$work = Join-Path ([IO.Path]::GetTempPath()) ('modsupport-build-' + [guid]::NewGuid().ToString('N'))
try {
    dotnet build (Join-Path $PSScriptRoot 'BattleTechPortBootstrap.csproj') -c Release -o $work "-p:ManagedDir=$Managed" "-p:ModTekLib=$(Join-Path $Bta 'Mods\ModTek\lib')" -nologo -v q
    if ($LASTEXITCODE) { throw 'The bootstrap did not build.' }
    $null = New-Item -ItemType Directory -Force $Dest
    Copy-Item (Join-Path $work 'BattleTechPortBootstrap.dll') $Dest
    Copy-Item (Join-Path $PSScriptRoot 'doorstop_config.ini') $Dest
    Copy-Item $Winhttp (Join-Path $Dest 'winhttp.dll')
    Copy-Item $DoorstopLicense (Join-Path $Dest 'DOORSTOP-LICENSE.txt')
    Copy-Item (Join-Path $PSScriptRoot 'ModSupport-README.txt') (Join-Path $Dest 'README.txt')
    $link = Join-Path (Split-Path $PSScriptRoot -Parent) 'bta-link'
    Copy-Item -LiteralPath (Join-Path $link 'Link BTA.cmd'), (Join-Path $link 'Link BTA (copy).cmd'), (Join-Path $link 'Link-BTA.ps1'), (Join-Path $link 'link-bta.sh') -Destination $Dest
}
finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
