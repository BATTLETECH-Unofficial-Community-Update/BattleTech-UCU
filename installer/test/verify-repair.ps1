# Verify install must make a repair replace what it found. An installed file is damaged in place with its size and write time
# kept, which the install record alone can't see: Verify names it, and the next run must rewrite that file and no other.
# Uses make.ps1's v1 and v2. -Setup is the installer under test; -Before runs an older one through the same steps and only
# reports (the control: its repair leaves the file damaged).
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
# An exe under tools/ can't write on the maintainer's workstation, and Verify writes the record: run copies from the root.
Copy-Item $Setup "$Root\tools\under-test.exe"; $Setup = "$Root\tools\under-test.exe"
if ($Before) { Copy-Item $Before "$Root\tools\before.exe"; $Before = "$Root\tools\before.exe" }
$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fail++ } }
$ErrorActionPreference = 'Continue'   # installers report failures on stderr; their exit codes are checked below
function Run([string]$exe, [string]$dest, [string]$pkg = 'pkg2') {
    Copy-Item $exe "$Root\$pkg\setup-under-test.exe" -Force
    $script:out = (& "$Root\$pkg\setup-under-test.exe" --retail $Retail --dest $dest --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | ForEach-Object { "$_" }) -join ' '
    $script:code = $LASTEXITCODE
}
function Verify([string]$exe, [string]$dest) {
    $script:out = (& $exe --verify $dest 2>&1 | ForEach-Object { "$_" }) -join ' '
    $script:code = $LASTEXITCODE
}
function Hash([string]$p) { (Get-FileHash -LiteralPath $p).Hash }
function Damage([string]$p) {
    $t = (Get-Item -LiteralPath $p).LastWriteTimeUtc
    $fs = [IO.File]::Open($p, 'Open', 'ReadWrite')
    try { $fs.Position = 5000; $b = $fs.ReadByte(); $fs.Position = 5000; $fs.WriteByte(($b -bxor 0xFF)) } finally { $fs.Dispose() }
    (Get-Item -LiteralPath $p).LastWriteTimeUtc = $t
}
$rel = 'BattleTech_Data\b.bin'; $want = Hash "$Root\p2\$rel"
if ($Before) {
    $d = "$Root\before"
    Run $Setup $d; Damage "$d\$rel"
    Verify $Before $d; $v1 = $code
    Run $Before $d; $r = $code; $same = $out
    Verify $Before $d
    "before: verify=$v1 repair=$r file=$(if ((Hash "$d\$rel") -eq $want) { 'repaired' } else { 'STILL DAMAGED' }) verify-again=$code"
}
$d = "$Root\after"
Run $Setup $d
Check "v2 installs (exit $code)" ($code -eq 0)
$rec = Hash "$d\.btu6-install.tsv"
Verify $Setup $d
Check "verify of a good install exits 0 and leaves the record alone (got $code)" ($code -eq 0 -and (Hash "$d\.btu6-install.tsv") -eq $rec)
Damage "$d\$rel"
Run $Setup $d
Check "a run without a verify still trusts the record (the cost of not rehashing 34 GB)" ($code -eq 0 -and (Hash "$d\$rel") -ne $want)
Verify $Setup $d
Check "verify names b.bin only and exits 4 (got $code)" ($code -eq 4 -and $out -match '\b1 of 10 files' -and $out -match 'b\.bin')
Check 'verify does not mark the folder unfinished' (-not (Test-Path "$d\.btu6-updating"))
$others = @{}; Get-ChildItem $d -Recurse -File | Where-Object { $_.Name -ne 'b.bin' -and $_.Name -notlike '.btu6*' -and $_.Name -ne 'ucu-manifest.tsv' } | ForEach-Object { $others[$_.FullName] = $_.LastWriteTimeUtc.Ticks }
Run $Setup $d
Check "repair exits 0 (got $code)" ($code -eq 0)
Check "repair rewrote b.bin" ((Hash "$d\$rel") -eq $want)
Check "repair kept the other 9 files ($($out -replace '.*?(\d+ were already up to date).*', '$1'))" ($out -match '\b9 were already up to date')
Check 'repair left the other files untouched on disk' (@($others.Keys | Where-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc.Ticks -ne $others[$_] }).Count -eq 0)
Verify $Setup $d
Check "verify after the repair exits 0 (got $code)" ($code -eq 0)
Check 'repair clears the mark' (-not (Test-Path "$d\.btu6-updating"))
# A missing file and a shortened one, in one go.
Remove-Item "$d\BattleTech_Data\sub\d.bin"
[IO.File]::WriteAllBytes("$d\BattleTech_Data\a.bin", [byte[]]::new(10))
Verify $Setup $d
Check "verify names a missing and a short file (got $code)" ($code -eq 4 -and $out -match '\b2 of 10 files' -and $out -match 'missing .*d\.bin' -and $out -match 'size .*a\.bin')
Run $Setup $d
$cmp = & (Join-Path $PSScriptRoot 'compare.ps1') -Install $d -Player "$Root\p2"
Check "repair restores both ($cmp)" ($code -eq 0 -and $cmp -match '^EQUAL:')
# v1's c.bin, which v2 drops: found damaged by Verify, or failed by a repair, it must still go with the update.
$d = "$Root\dropped"; $c = "$d\BattleTech_Data\c.bin"
Run $Setup $d 'pkg1'
Damage $c
Verify $Setup $d
Check "verify names c.bin (got $code)" ($code -eq 4 -and $out -match 'c\.bin')
Run $Setup $d
Check "updating after that verify deletes the dropped c.bin (got $code)" ($code -eq 0 -and -not (Test-Path $c))
$d = "$Root\failed"; $c = "$d\BattleTech_Data\c.bin"
Run $Setup $d 'pkg1'
robocopy "$Root\pkg1" "$Root\pkg1Bad" /E /NFL /NDL /NJH /NJS /NP | Out-Null
# A byte flipped in every blob pkg2 doesn't have (c.bin's and v1's a.bin's).
$had = @(Get-ChildItem "$Root\pkg2" -Recurse -File | ForEach-Object Name)
Get-ChildItem "$Root\pkg1Bad\data" -File | Where-Object { $had -notcontains $_.Name } | ForEach-Object { Damage $_.FullName }
Damage $c
Verify $Setup $d
Run $Setup $d 'pkg1Bad'
Check "a repair from a damaged package fails on c.bin (got $code)" ($code -eq 4 -and (Test-Path $c))
Run $Setup $d
$cmp = & (Join-Path $PSScriptRoot 'compare.ps1') -Install $d -Player "$Root\p2"
Check "updating after that failure deletes the dropped c.bin (got $code, $cmp)" ($code -eq 0 -and -not (Test-Path $c) -and $cmp -match '^EQUAL:')
if ($fail) { "FAILED $fail"; exit 1 } else { 'ALL OK'; exit 0 }
