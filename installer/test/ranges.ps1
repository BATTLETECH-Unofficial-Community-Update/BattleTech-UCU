# Retail-range packing (ReleasePack --gog): a small player whose own files embed copied runs of real retail files is
# packed as retail byte ranges plus residual blobs, installed from Steam and from GOG, and every player file compared.
#   own.bin      two 1.2/1.4 MiB runs of BattleTech_Data/level10 (Steam and GOG bytes differ), random bytes between:
#                a Steam ranged entry, and a GOG section entry because GOG's bytes at those ranges don't match
#   shared.bin   two runs of a GI file byte-equal in both stores: identical pieces on both, so no GOG section entry
#   lowentropy.bin  3 MiB of zeros, 3 MiB of a repeating 5-byte pattern and one real retail run (1.3 MiB of level10):
#                the pack must finish in minutes (low-entropy data once made the scan re-read retail at every byte
#                position) and the file installs byte-identical
# Installs: Steam and GOG, every player file equal (SHA-256), Verify install passes.
# Damaged retail: a scratch retail holding the fingerprint's files and the range sources, one byte flipped inside a
# used run: shared.bin fails, own.bin and the rest still install (exit 4).
# No GOG run: small installs of random bytes (synth.ps1) and a file whose runs only the Steam install holds, packed locked and
# unlocked with --gog: both stores install it, and a later pack reuses the package.
# Control: a player with no matching runs packs the same vault format and installs; an installer from before vault v4
# (-OldInstaller, 2.1.5-2B's) refuses that package and writes nothing.
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Steam,
    [string]$Gog,
    [string]$Bin,
    [string]$OldInstaller,
    [string]$Level = 'BattleTech_Data/level10',
    [string]$Shared = 'BattleTech_Data/GI/level28/30/3014f4066de989b58ab61355a0881953.rsc.sse'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\UcuPaths.ps1')
if (-not $Bin) { $Bin = Join-Path $UcuRepo 'tools\release-gate\bin' }
if (-not $Steam) { $Steam = Get-UcuPath retail_install -Required }
if (-not $Gog) { $Gog = Join-Path (Split-Path $UcuWorkspace -Parent) 'Games\BATTLETECH' }
if (-not (Test-Path (Join-Path $Gog 'goggame-1482783682.info'))) { throw "Not a GOG BATTLETECH install (no goggame-1482783682.info): $Gog" }
if (-not $OldInstaller) { $OldInstaller = Join-Path $UcuWorkspace 'builds\release-2.1.5-2B-package\BattleTechUCUSetup.exe' }
if (-not (Test-Path $OldInstaller)) { throw "No older installer to test with: $OldInstaller" }
if (Test-Path $Root) { throw "Root exists: $Root" }
New-Item -ItemType Directory -Force "$Root\tools", "$Root\no-steam" | Out-Null
Copy-Item "$Bin\*.exe" "$Root\tools"
Copy-Item $OldInstaller "$Root\tools\BattleTechUCUSetup-2.1.5-2B.exe"
$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fail++ } }
function Sha([string]$p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant() }
function ReadRun([string]$path, [long]$off, [int]$len) {
    $b = [byte[]]::new($len)
    $f = [IO.File]::OpenRead($path)
    try { $f.Position = $off; $got = 0; while ($got -lt $len) { $n = $f.Read($b, $got, $len - $got); if ($n -le 0) { throw "short read: $path" }; $got += $n } }
    finally { $f.Dispose() }
    return ,$b
}
function MakeFile([string]$dst, [byte[]]$run1, [byte[]]$run2, [int]$seed) {
    $gap1 = 100003; $gap2 = 73727
    $buf = [byte[]]::new($gap1 + $run1.Length + $gap2 + $run2.Length + 50021)
    ([Random]::new($seed)).NextBytes($buf)
    [Array]::Copy($run1, 0, $buf, $gap1, $run1.Length)
    [Array]::Copy($run2, 0, $buf, $gap1 + $run1.Length + $gap2, $run2.Length)
    [IO.File]::WriteAllBytes($dst, $buf)
    return @($gap1, $gap1 + $run1.Length + $gap2)
}
function InstalledMatches([string]$dest, [string]$player) {
    $bad = @()
    Get-ChildItem -Recurse -File $player | ForEach-Object {
        $rel = $_.FullName.Substring($player.TrimEnd('\').Length + 1)
        $t = Join-Path $dest $rel
        if (-not (Test-Path $t) -or (Sha $t) -ne (Sha $_.FullName)) { $bad += $rel }
    }
    return $bad
}

# --- the player: two own files, each embedding two runs of a real retail file ---
$player = "$Root\player"
New-Item -ItemType Directory -Force $player | Out-Null
Copy-Item (Join-Path $Steam 'BattleTech.exe') (Join-Path $player 'BattleTech.exe')
[IO.File]::WriteAllText("$player\steam_appid.txt", '637090')
$l1 = 1200000; $l2 = 1400000
$levelOff1 = 4000123; $levelOff2 = 22000777
$lv = Join-Path $Steam $Level
$sh = Join-Path $Steam $Shared
if (-not (Test-Path $lv) -or -not (Test-Path $sh)) { throw "Test sources are missing: $Level / $Shared" }
$sharedOff1 = 123457; $sharedOff2 = 3500003
$run = ReadRun $lv $levelOff1 $l1; $p1 = MakeFile "$player\own.bin" $run (ReadRun $lv $levelOff2 $l2) 1
$run = ReadRun $sh $sharedOff1 $l1; $p2 = MakeFile "$player\shared.bin" $run (ReadRun $sh $sharedOff2 $l2) 2
# Low-entropy filler: zeros and a short repeating pattern, around one genuine retail run.
$ms = [IO.MemoryStream]::new()
$rnd = [Random]::new(5)
$x = [byte[]]::new(100003)
$rnd.NextBytes($x); $ms.Write($x, 0, $x.Length)
$z = [byte[]]::new(3MB); $ms.Write($z, 0, $z.Length)
$rnd.NextBytes($x); $ms.Write($x, 0, $x.Length)
$pat = [byte[]]::new(3MB); for ($i = 0; $i -lt $pat.Length; $i++) { $pat[$i] = [byte](1 + ($i % 5)) }; $ms.Write($pat, 0, $pat.Length)
$rnd.NextBytes($x); $ms.Write($x, 0, $x.Length)
$lowRun = ReadRun $lv 13000333 1300000
$ms.Write($lowRun, 0, $lowRun.Length)
$rnd.NextBytes($x); $ms.Write($x, 0, $x.Length)
[IO.File]::WriteAllBytes("$player\lowentropy.bin", $ms.ToArray())
$ms.Dispose()
foreach ($o in $p1 + $p2) { if ($o % 4096 -eq 0) { throw "A copied run starts at an aligned offset ($o): the format wants unaligned runs" } }

# --- pack with --gog ---
$packClock = [Diagnostics.Stopwatch]::StartNew()
$packOut = & "$Root\tools\ReleasePack.exe" --player $player --retail $Steam --gog $Gog --key "$Root\scratch.key" --out "$Root\pkg" 2>&1 | Out-String
$packClock.Stop()
Check "pack exit 0 (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
Check "pack with low-entropy data took $([int]$packClock.Elapsed.TotalSeconds) s (limit 300)" ($packClock.Elapsed.TotalSeconds -lt 300)
$packOut.Trim() | ForEach-Object { "  pack: $_" }
Check 'pack reports ranged files' ($packOut -match 'ranged [1-9]')
Check 'lowentropy.bin was ranged too (3 ranged files)' ($packOut -match 'ranged 3 files')
$magic = [Text.Encoding]::ASCII.GetString(([IO.File]::ReadAllBytes("$Root\pkg\package.vault"))[0..7])
Check "vault magic is BTU6GAR4 (got $magic)" ($magic -eq 'BTU6GAR4')
Check 'GOG manifest written' (Test-Path "$Root\pkg\ucu-manifest-gog.tsv")

$ErrorActionPreference = 'Continue'   # installers report failures on stderr; their exit codes are checked below
function TestStore([string]$store, [string]$retail) {
    $dest = "$Root\install-$store"
    & "$Root\pkg\BattleTechUCUSetup.exe" --retail $retail --dest $dest --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | ForEach-Object { "  setup: $_" }
    Check "$store install exit 0 (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
    $bad = InstalledMatches $dest $player
    Check "$store every player file matches ($($bad -join ', '))" ($bad.Count -eq 0)
    & "$Root\tools\BattleTechUCUSetup.exe" --verify $dest 2>&1 | ForEach-Object { "  verify: $_" }
    Check "$store verify exit 0 (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
    Check "$store kept the new vault's ranges (own.bin matches)" ((Sha "$dest\own.bin") -eq (Sha "$player\own.bin"))
    Check "$store shared.bin matches" ((Sha "$dest\shared.bin") -eq (Sha "$player\shared.bin"))
}
TestStore 'steam' $Steam
TestStore 'gog' $Gog
# Steam install has exactly the player's files (no GOG Galaxy files, no DLC markers).
$cmp = & (Join-Path $PSScriptRoot 'compare.ps1') -Install "$Root\install-steam" -Player $player
Check "steam install equals the player exactly ($cmp)" ($cmp -match '^EQUAL:')
Check 'gog install has no base-game marker' (-not (Test-Path "$Root\install-gog\goggame-1482783682.info"))

# --- a damaged retail: a copy of the Steam install with one byte flipped inside a used range ---
# The copied runs can resolve to any retail file holding the same bytes (the matcher keeps the first hash hit), so the
# scratch retail is a full copy: a minimal one would miss a source the packer legitimately chose.
$scratch = "$Root\retail-damaged"
if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
robocopy $Steam $scratch /E /MT:16 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy of the retail install failed: $LASTEXITCODE" }
$global:LASTEXITCODE = 0   # robocopy's 1 to 7 are success; the global, so later exit codes are not hidden by a script-scope copy
$flip = Join-Path $scratch $Shared
$fs = [IO.File]::Open($flip, 'Open', 'ReadWrite')
try { $fs.Position = $sharedOff1 + 500000; $b = $fs.ReadByte(); $fs.Position = $sharedOff1 + 500000; $fs.WriteByte(($b -bxor 0xFF)) }
finally { $fs.Dispose() }
& "$Root\pkg\BattleTechUCUSetup.exe" --retail $scratch --dest "$Root\install-damaged" --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | ForEach-Object { "  setup: $_" }
Check "damaged retail install exit 4, a file failed its check (got $LASTEXITCODE)" ($LASTEXITCODE -eq 4)
Check 'damaged retail: the file using the flipped range did not install' (-not (Test-Path "$Root\install-damaged\shared.bin"))
Check 'damaged retail: own.bin still installed' ((Test-Path "$Root\install-damaged\own.bin") -and (Sha "$Root\install-damaged\own.bin") -eq (Sha "$player\own.bin"))
Check 'damaged retail: BattleTech.exe still installed' ((Test-Path "$Root\install-damaged\BattleTech.exe") -and (Sha "$Root\install-damaged\BattleTech.exe") -eq (Sha "$player\BattleTech.exe"))

# --- control: no matching runs, same format; an older installer refuses it ---
$control = "$Root\control"
$controlPlayer = "$control\player"
New-Item -ItemType Directory -Force $controlPlayer | Out-Null
Copy-Item (Join-Path $Steam 'BattleTech.exe') (Join-Path $controlPlayer 'BattleTech.exe')
[IO.File]::WriteAllText("$controlPlayer\steam_appid.txt", '637090')
$random = [byte[]]::new(2500021); ([Random]::new(3)).NextBytes($random)
[IO.File]::WriteAllBytes("$controlPlayer\own.bin", $random)
& "$Root\tools\ReleasePack.exe" --player $controlPlayer --retail $Steam --key "$Root\scratch.key" --out "$control\pkg" 2>&1 | ForEach-Object { "  pack: $_" }
Check "control pack exit 0 (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
$magic = [Text.Encoding]::ASCII.GetString(([IO.File]::ReadAllBytes("$control\pkg\package.vault"))[0..7])
Check "control vault magic is BTU6GAR4 too (got $magic)" ($magic -eq 'BTU6GAR4')
Copy-Item "$Root\tools\BattleTechUCUSetup-2.1.5-2B.exe" "$control\pkg\old-setup.exe"
& "$control\pkg\old-setup.exe" --retail $Steam --dest "$control\install" --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | ForEach-Object { "  old setup: $_" }
Check "old installer refuses the control package, writing nothing (exit $LASTEXITCODE)" ($LASTEXITCODE -ne 0 -and -not (Test-Path "$control\install"))
& "$control\pkg\BattleTechUCUSetup.exe" --retail $Steam --dest "$control\install" --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | ForEach-Object { "  setup: $_" }
Check "its own installer installs it (exit $LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and (Sha "$control\install\own.bin") -eq (Sha "$controlPlayer\own.bin") -and (Sha "$control\install\BattleTech.exe") -eq (Sha "$controlPlayer\BattleTech.exe"))

# --- a ranged file with no run in the GOG install ---
. (Join-Path $PSScriptRoot 'synth.ps1')
$nr = "$Root\no-run"
New-SynthInstalls $nr
New-Item -ItemType Directory -Force "$nr\player" | Out-Null
Copy-Item "$nr\steam\BattleTech.exe" "$nr\player"
[IO.File]::WriteAllText("$nr\player\steam_appid.txt", '637090')
$run = ReadRun "$nr\steam\BattleTech_Data\level10" 400123 $l1
$null = MakeFile "$nr\player\steamonly.bin" $run (ReadRun "$nr\steam\BattleTech_Data\level10" 2000777 $l2) 7
[IO.File]::WriteAllText("$nr\master.txt", '5a' * 32)
foreach ($form in 'unlocked', 'locked') {
    $pkg = "$nr\pkg-$form"; $lock = @(); $open = @()
    if ($form -eq 'locked') { $lock = @('--master-secret', "$nr\master.txt", '--unlock-url', 'https://example.invalid') }
    $packOut = & "$Root\tools\ReleasePack.exe" --player "$nr\player" --retail "$nr\steam" --gog "$nr\gog" --key "$Root\scratch.key" --out $pkg @lock 2>&1 | Out-String
    Check "no GOG run, ${form}: pack exit 0, ranged from Steam only (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and $packOut -match 'ranged 1 files' -and $packOut -match '\b1 ranged files from GOG''s own ranges')
    if ($form -eq 'locked') {
        $id = -join ([IO.File]::ReadAllBytes("$pkg\package.vault")[9..24] | ForEach-Object { $_.ToString('x2') })
        $h = New-Object Security.Cryptography.HMACSHA256 (,([byte[]](@(0x5a) * 32)))
        [IO.File]::WriteAllText("$nr\share.txt", (-join ($h.ComputeHash([Text.Encoding]::UTF8.GetBytes("btucu-share|$id")) | ForEach-Object { $_.ToString('x2') })))
        $open = @('--share-file', "$nr\share.txt")
    }
    foreach ($store in 'steam', 'gog') {
        $dest = "$nr\install-$form-$store"
        & "$pkg\BattleTechUCUSetup.exe" --retail "$nr\$store" --dest $dest --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" @open 2>&1 | ForEach-Object { "  setup: $_" }
        Check "no GOG run, ${form}: installs from $store (exit $LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and @(InstalledMatches $dest "$nr\player").Count -eq 0)
    }
    $packOut = & "$Root\tools\ReleasePack.exe" --player "$nr\player" --retail "$nr\steam" --gog "$nr\gog" --key "$Root\scratch.key" --out "$pkg-again" --previous $pkg @lock 2>&1 | Out-String
    Check "no GOG run, ${form}: a later pack over it seals nothing anew (exit $LASTEXITCODE)" ($LASTEXITCODE -eq 0 -and $packOut -match '\b0 sealed anew')
}

if ($fail) { "FAILED $fail"; exit 1 } else { 'ALL OK'; exit 0 }
