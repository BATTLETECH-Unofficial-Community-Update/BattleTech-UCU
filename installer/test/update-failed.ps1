# An update that fails its check must say so, remove the files the new version dropped, verify against the new manifest and
# mark the folder unfinished; a repair with the good package must clear the mark and leave exactly the new version.
# Uses make.ps1's v1 and v2 (v2 changes a.bin, drops c.bin, adds e.bin). -Setup is the installer under test; -Before runs an
# older one through the same damaged update and only reports what it leaves (the control: no mark, c.bin still there).
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Retail,
    [string]$Setup,
    [string]$Before
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\UcuPaths.ps1')
if (-not $Retail) { $Retail = Get-UcuPath retail_install -Required }
if (-not $Setup) { $Setup = Join-Path $UcuRepo 'tools\release-gate\bin\BattleTechUCUSetup.exe' }
if (Test-Path $Root) { throw "Root exists: $Root" }
& (Join-Path $PSScriptRoot 'make.ps1') -Root $Root -Retail $Retail | Out-Null
New-Item -ItemType Directory -Force "$Root\no-steam" | Out-Null
$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fail++ } }
$ErrorActionPreference = 'Continue'   # installers report failures on stderr; their exit codes are checked below
function Run([string]$exe, [string]$pkg, [string]$dest) {
    Copy-Item $exe "$pkg\setup-under-test.exe" -Force
    $script:out = (& "$pkg\setup-under-test.exe" --retail $Retail --dest $dest --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | Out-String).Trim()
    $script:code = $LASTEXITCODE
}
# The damaged package: one byte of the blob holding the new a.bin flipped (the blob pkg1 doesn't have, nearest 2 MiB).
robocopy "$Root\pkg2" "$Root\pkgBad" /E /NFL /NDL /NJH /NJS /NP | Out-Null
$had = @(Get-ChildItem "$Root\pkg1" -Recurse -File | ForEach-Object Name)
$blob = Get-ChildItem "$Root\pkgBad\data" -File | Where-Object { $had -notcontains $_.Name } | Sort-Object { [Math]::Abs($_.Length - 2MB) } | Select-Object -First 1
$fs = [IO.File]::Open($blob.FullName, 'Open', 'ReadWrite')
try { $fs.Position = 100000; $b = $fs.ReadByte(); $fs.Position = 100000; $fs.WriteByte(($b -bxor 0xFF)) } finally { $fs.Dispose() }

function Leaves([string]$d) {
    "exit=$code mark=$(Test-Path "$d\.btu6-updating") c.bin=$(Test-Path "$d\BattleTech_Data\c.bin") e.bin=$(Test-Path "$d\BattleTech_Data\e.bin") a.bin=$(if ((Get-FileHash "$d\BattleTech_Data\a.bin").Hash -eq (Get-FileHash "$Root\p1\BattleTech_Data\a.bin").Hash) { 'v1' } else { 'other' })"
}
if ($Before) {
    Run $Setup "$Root\pkg1" "$Root\before"
    Run $Before "$Root\pkgBad" "$Root\before"
    "before: $(Leaves "$Root\before")"
}
$d = "$Root\after"
Run $Setup "$Root\pkg1" $d
Check "v1 installs (exit $code), no mark" ($code -eq 0 -and -not (Test-Path "$d\.btu6-updating"))
Run $Setup "$Root\pkgBad" $d
"after:  $(Leaves $d)"
Check "damaged update exits 4 (got $code)" ($code -eq 4)
Check 'damaged update says the folder mixes two versions' ($out -match 'mixes two versions')
Check 'damaged update leaves the mark' (Test-Path "$d\.btu6-updating")
Check 'damaged update removed the dropped c.bin' (-not (Test-Path "$d\BattleTech_Data\c.bin"))
Check 'damaged update installed e.bin' (Test-Path "$d\BattleTech_Data\e.bin")
$v = (& $Setup --verify $d 2>&1 | Out-String).Trim(); $vc = $LASTEXITCODE
Check "verify names only a.bin ($v)" ($vc -eq 4 -and $v -match '\b1 of 10 files' -and $v -match 'a\.bin')
Run $Setup "$Root\pkg2" $d
Check "repair exits 0 (got $code)" ($code -eq 0)
Check 'repair clears the mark' (-not (Test-Path "$d\.btu6-updating"))
$cmp = & (Join-Path $PSScriptRoot 'compare.ps1') -Install $d -Player "$Root\p2"
Check "repaired install equals v2 ($cmp)" ($cmp -match '^EQUAL:')
& $Setup --verify $d *> $null
Check "repaired install verifies (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
Run $Setup "$Root\pkg2" $d
Check "a rerun with nothing to do exits 0, no mark (got $code)" ($code -eq 0 -and -not (Test-Path "$d\.btu6-updating"))
# A fresh install that fails has no earlier version to mix with.
Run $Setup "$Root\pkgBad" "$Root\fresh"
Check "fresh damaged install: exit 4, marked, no mixed-version text (got $code)" ($code -eq 4 -and (Test-Path "$Root\fresh\.btu6-updating") -and $out -notmatch 'mixes two versions')
if ($fail) { "FAILED $fail"; exit 1 } else { 'ALL OK'; exit 0 }
