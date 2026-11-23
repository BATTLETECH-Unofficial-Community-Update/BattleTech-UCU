# ReleasePack --previous: a pack that reuses an earlier package's scans and blobs must give the package a full pack gives.
#   v1 player: BattleTech.exe, own.bin (retail runs of level10: Steam and GOG bytes differ), shared.bin (runs of a GI file both
#              stores share), rand.bin and small.bin (sealed whole; small is under 64 KiB), change.bin, gone.bin
#   v2 player: change.bin rewritten, gone.bin deleted, new.bin (a fresh retail run: needs a scan) and added.bin added
# Checks: reuse is reported and counted; data/ (names and bytes) and both public manifests equal a full pack of v2; Steam and GOG
# installs from the reusing package equal v2; blobs are hard-linked; a damaged old blob is sealed anew instead of trusted;
# a package from before ranges, another content key, a v3 new package and a v3 old package behave as documented.
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Steam,
    [string]$Gog,
    [string]$Bin,
    [string]$Level = 'BattleTech_Data/level10',
    [string]$Shared = 'BattleTech_Data/GI/level28/30/3014f4066de989b58ab61355a0881953.rsc.sse'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\UcuPaths.ps1')
if (-not $Bin) { $Bin = Join-Path $UcuRepo 'tools\release-gate\bin' }
if (-not $Steam) { $Steam = Get-UcuPath retail_install -Required }
if (-not $Gog) { $Gog = Join-Path (Split-Path $UcuWorkspace -Parent) 'Games\BATTLETECH' }
if (-not (Test-Path (Join-Path $Gog 'goggame-1482783682.info'))) { throw "Not a GOG BATTLETECH install: $Gog" }
if (Test-Path $Root) { throw "Root exists: $Root" }
New-Item -ItemType Directory -Force "$Root\tools", "$Root\no-steam" | Out-Null
Copy-Item "$Bin\*.exe" "$Root\tools"
$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fail++ } }
function Sha([string]$p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant() }
function ReadRun([string]$path, [long]$off, [int]$len) {
    $b = [byte[]]::new($len)
    $f = [IO.File]::OpenRead($path)
    try { $f.Position = $off; $got = 0; while ($got -lt $len) { $n = $f.Read($b, $got, $len - $got); if ($n -le 0) { throw "short read" }; $got += $n } }
    finally { $f.Dispose() }
    return ,$b
}
function Random([string]$dst, [int]$size, [int]$seed) { $b = [byte[]]::new($size); ([Random]::new($seed)).NextBytes($b); [IO.File]::WriteAllBytes($dst, $b) }
function WithRuns([string]$dst, [byte[]]$run1, [byte[]]$run2, [int]$seed) {
    $buf = [byte[]]::new(100003 + $run1.Length + 73727 + $run2.Length + 50021)
    ([Random]::new($seed)).NextBytes($buf)
    [Array]::Copy($run1, 0, $buf, 100003, $run1.Length)
    [Array]::Copy($run2, 0, $buf, 100003 + $run1.Length + 73727, $run2.Length)
    [IO.File]::WriteAllBytes($dst, $buf)
}
function Pack([string]$player, [string]$out, [string[]]$more) {
    $script:last = & "$Root\tools\ReleasePack.exe" --player $player --retail $Steam --gog $Gog --out $out @more 2>&1 | Out-String
    $script:lastExit = $LASTEXITCODE
    $script:last.Trim() | ForEach-Object { "  pack: $_" }
}
function Listing([string]$pkg) { Get-ChildItem "$pkg\data" | Sort-Object Name | ForEach-Object { "$($_.Name) $(Sha $_.FullName)" } }
function InstalledMatches([string]$dest, [string]$player) {
    $bad = @()
    Get-ChildItem -Recurse -File $player | ForEach-Object {
        $rel = $_.FullName.Substring($player.TrimEnd('\').Length + 1)
        $t = Join-Path $dest $rel
        if (-not (Test-Path $t) -or (Sha $t) -ne (Sha $_.FullName)) { $bad += $rel }
    }
    return $bad
}

# --- the players ---
$v1 = "$Root\v1"; New-Item -ItemType Directory -Force $v1 | Out-Null
Copy-Item (Join-Path $Steam 'BattleTech.exe') $v1
[IO.File]::WriteAllText("$v1\steam_appid.txt", '637090')
$lv = Join-Path $Steam $Level; $sh = Join-Path $Steam $Shared
WithRuns "$v1\own.bin" (ReadRun $lv 4000123 1200000) (ReadRun $lv 22000777 1400000) 1
WithRuns "$v1\shared.bin" (ReadRun $sh 123457 1200000) (ReadRun $sh 3500003 1400000) 2
Random "$v1\rand.bin" 2500021 3; Random "$v1\small.bin" 20011 4; Random "$v1\change.bin" 700001 5; Random "$v1\gone.bin" 300007 6
$v2 = "$Root\v2"; Copy-Item -Recurse $v1 $v2
Random "$v2\change.bin" 700001 15; Remove-Item "$v2\gone.bin"
WithRuns "$v2\new.bin" (ReadRun $lv 9000211 1300000) (ReadRun $lv 15000999 1250000) 7
Random "$v2\added.bin" 900001 8

$ErrorActionPreference = 'Continue'
$key = "$Root\scratch.key"
Pack $v1 "$Root\pkgA" @('--key', $key)
Check "pack of v1 exit 0 (got $lastExit)" ($lastExit -eq 0)
Pack $v2 "$Root\pkgFull" @('--key', $key)
Check "full pack of v2 exit 0 (got $lastExit)" ($lastExit -eq 0)
Pack $v2 "$Root\pkgPrev" @('--key', $key, '--previous', "$Root\pkgA")
Check "pack of v2 with --previous exit 0 (got $lastExit)" ($lastExit -eq 0)
Check 'summary reports 5 files that kept their scan (own, shared, rand, small, steam_appid.txt)' ($last -match '5 files kept their scan')
$anew = if ($last -match '(\d+) sealed anew') { [int]$Matches[1] } else { -1 }
Check "summary counts blobs sealed anew (${anew}; change, the residual of new, added)" ($anew -eq 3)
$a = Listing "$Root\pkgFull"; $b = Listing "$Root\pkgPrev"
Check "data/ equals a full pack: $($a.Count) blobs, same names and bytes" ($a.Count -gt 0 -and (-not (Compare-Object $a $b)))
Check 'public manifest equals a full pack' ((Sha "$Root\pkgFull\ucu-manifest.tsv") -eq (Sha "$Root\pkgPrev\ucu-manifest.tsv"))
Check 'GOG manifest equals a full pack' ((Sha "$Root\pkgFull\ucu-manifest-gog.tsv") -eq (Sha "$Root\pkgPrev\ucu-manifest-gog.tsv"))
$linked = 0
Get-ChildItem "$Root\pkgPrev\data" | ForEach-Object { if ((fsutil hardlink list $_.FullName | Where-Object { $_ }).Count -gt 1) { $linked++ } }
Check "reused blobs are hard-linked ($linked)" ($linked -ge 4)

foreach ($s in @(@('steam', $Steam), @('gog', $Gog))) {
    $dest = "$Root\install-$($s[0])"
    & "$Root\pkgPrev\BattleTechUCUSetup.exe" --retail $s[1] --dest $dest --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | ForEach-Object { "  setup: $_" }
    Check "$($s[0]) install from the reusing package exit 0 (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
    $bad = InstalledMatches $dest $v2
    Check "$($s[0]) every v2 file matches ($($bad -join ', '))" ($bad.Count -eq 0)
    & "$Root\tools\BattleTechUCUSetup.exe" --verify $dest 2>&1 | ForEach-Object { "  verify: $_" }
    Check "$($s[0]) verify exit 0 (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
}

# A damaged old blob is never linked in: it is sealed anew and the package still equals a full pack.
robocopy "$Root\pkgA" "$Root\pkgDamaged" /E /NFL /NDL /NJH /NJS /NP | Out-Null
$LASTEXITCODE = 0
$victim = Get-ChildItem "$Root\pkgDamaged\data" | Sort-Object Length -Descending | Select-Object -First 1
$fs = [IO.File]::Open($victim.FullName, 'Open', 'ReadWrite'); $fs.Position = 100; $x = $fs.ReadByte(); $fs.Position = 100; $fs.WriteByte(($x -bxor 0xFF)); $fs.Dispose()
Pack $v2 "$Root\pkgPrevDamaged" @('--key', $key, '--previous', "$Root\pkgDamaged")
Check "pack over a damaged previous blob exit 0 (got $lastExit)" ($lastExit -eq 0)
Check 'exactly one more blob was sealed anew' ($last -match "$($anew + 1) sealed anew")
Check 'damaged-previous package data/ equals a full pack' (-not (Compare-Object $a (Listing "$Root\pkgPrevDamaged")))

# Refusals
Random "$Root\ctl.bin" 2500021 9
$ctl = "$Root\ctl"; New-Item -ItemType Directory -Force $ctl | Out-Null
Copy-Item "$v1\BattleTech.exe", "$v1\steam_appid.txt" $ctl; Copy-Item "$Root\ctl.bin" "$ctl\own.bin"
Pack $ctl "$Root\pkgOld" @('--key', $key)
Pack $v2 "$Root\pkgRefused1" @('--key', $key, '--previous', "$Root\pkgOld")
Check "a previous package from before ranges is refused (exit $lastExit)" ($lastExit -ne 0 -and $last -match 'before retail-range packing')
"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" | Set-Content -NoNewline "$Root\other.key"
Pack $v2 "$Root\pkgRefused2" @('--key', "$Root\other.key", '--previous', "$Root\pkgA")
Check "another content key is refused (exit $lastExit)" ($lastExit -ne 0 -and $last -match 'different content key')
Check 'no half-made package after a refusal' (-not (Test-Path "$Root\pkgRefused2\package.vault"))

# v3: a new v3 package over a v2 one, then a v3 package over that v3 one (needs the master secret to read)
"fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210" | Set-Content -NoNewline "$Root\master.txt"
$v3 = @('--key', $key, '--master-secret', "$Root\master.txt", '--unlock-url', 'https://example.invalid/unlock')
Pack $v2 "$Root\pkg3Full" $v3
Pack $v2 "$Root\pkg3" ($v3 + @('--previous', "$Root\pkgA"))
Check "v3 package over a v2 previous exit 0 (got $lastExit)" ($lastExit -eq 0 -and $last -match 'reused from')
Check 'v3 reusing package data/ equals a full v3 pack' (-not (Compare-Object (Listing "$Root\pkg3Full") (Listing "$Root\pkg3")))
Pack $v2 "$Root\pkg3b" ($v3 + @('--previous', "$Root\pkg3"))
Check "v3 package over a v3 previous exit 0 (got $lastExit)" ($lastExit -eq 0 -and $last -match 'reused from')
Pack $v2 "$Root\pkg3c" @('--key', $key, '--previous', "$Root\pkg3")
Check "a v3 previous without the master secret is refused (exit $lastExit)" ($lastExit -ne 0 -and $last -match 'master secret')

if ($fail) { "FAILED $fail"; exit 1 } else { 'ALL OK'; exit 0 }
