# GOG installs (ReleasePack --gog): packs a small player against the Steam and GOG installs, installs it from each with no
# window, and checks what each install holds and that Verify install passes. The player has:
#   BattleTech.exe              byte-equal in both stores: copied from retail on both
#   same.json                   a retail data file byte-equal in both stores: copied on both
#   emblem.dds                  a retail file whose GOG bytes differ: Steam's copied on Steam; on GOG, GOG's own copy when
#                               gog-accept.tsv lists it, else Steam's bytes from the package
#   own.bin                     the player's own file: packed
#   dialog_m010.bnk             a large sound bank byte-equal in both stores: a hard link to the store's own file when the
#                               install is on that store's drive, else a copy
# and a GOG install also gets Galaxy64.dll and GalaxyCSharpGlue.dll from GOG's root, plus the user's own DLC markers
# (goggame-<dlc id>.info, never the base game's). A repair re-syncs them: a stale BATTLETECH DLC marker goes, another
# game's marker stays.
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Steam = 'D:\Steam\steamapps\common\BATTLETECH',
    [string]$Gog = 'F:\Games\BATTLETECH',
    [string]$Bin = 'F:\Unity-Tools\BattleTech-Unity6\tools\release-gate\bin',
    [string]$Emblem = 'BattleTech_Data/StreamingAssets/emblems/player/envTxrHrld_001.dds',
    [string]$Same = 'BattleTech_Data/StreamingAssets/data/constants/CombatGameConstants.json',
    [string]$Bank = 'BattleTech_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows/English(US)/dialog_m010.bnk'
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Root) { throw "Root exists: $Root" }
New-Item -ItemType Directory -Force "$Root\tools", "$Root\no-steam" | Out-Null
Copy-Item "$Bin\*.exe" "$Root\tools"
function Sha([string]$p) { (Get-FileHash -Algorithm SHA256 $p).Hash.ToLowerInvariant() }
function Put([string]$from, [string]$rel) {
    $to = Join-Path "$Root\player" $rel
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    Copy-Item $from $to
}
Put "$Steam\BattleTech.exe" 'BattleTech.exe'
[IO.File]::WriteAllText("$Root\player\steam_appid.txt", '637090')
Put (Join-Path $Steam $Same) $Same
Put (Join-Path $Steam $Emblem) $Emblem
Put (Join-Path $Steam $Bank) $Bank
$b = New-Object byte[] (1MB); (New-Object Random 7).NextBytes($b)
[IO.File]::WriteAllBytes("$Root\player\own.bin", $b)
if ((Sha (Join-Path $Steam $Same)) -ne (Sha (Join-Path $Gog $Same))) { throw "$Same differs between the stores: pick another" }
if ((Sha (Join-Path $Steam $Emblem)) -eq (Sha (Join-Path $Gog $Emblem))) { throw "$Emblem is the same in both stores: pick another" }
if ((Sha (Join-Path $Steam $Bank)) -ne (Sha (Join-Path $Gog $Bank))) { throw "$Bank differs between the stores: pick another" }
if ((Get-Item (Join-Path $Gog $Bank)).Length -lt 1MB) { throw "$Bank is under the 1 MB link floor: pick another" }
# The other names of a file (fsutil lists them without the drive letter); a hard link to retail shares retail's name.
function Names([string]$p) { @(fsutil hardlink list ([IO.Path]::GetFullPath($p))) | ForEach-Object { $_.Trim().ToLowerInvariant() } }
function LinkedTo([string]$p, [string]$retailFile) {
    $r = [IO.Path]::GetFullPath($retailFile); (Names $p) -contains $r.Substring([IO.Path]::GetPathRoot($r).Length - 1).ToLowerInvariant()
}
function SameDrive([string]$a, [string]$b) { [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($a)) -eq [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($b)) }

# A player carrying a GOG-only file beside the exe is refused: it would put a Steam install in GOG mode.
$ErrorActionPreference = 'Continue'   # the refusal is on stderr
foreach ($n in 'Galaxy64.dll', 'goggame-1314028793.info') {
    Copy-Item "$Gog\$n" "$Root\player\$n"
    & "$Root\tools\ReleasePack.exe" --player "$Root\player" --retail $Steam --gog $Gog --key "$Root\key.txt" --out "$Root\pkg-refused" 2>&1 | Out-Null
    $refused = $LASTEXITCODE -ne 0 -and -not (Test-Path "$Root\pkg-refused")
    Remove-Item "$Root\player\$n"
    if (-not $refused) { throw "pack accepted a player carrying $n" }
    "ok   pack refuses a player carrying $n"
}
$ErrorActionPreference = 'Stop'
& "$Root\tools\ReleasePack.exe" --player "$Root\player" --retail $Steam --gog $Gog --key "$Root\key.txt" --out "$Root\pkg"
if ($LASTEXITCODE -ne 0) { throw 'pack failed' }
$ErrorActionPreference = 'Continue'   # the installer reports on stderr; exit codes are checked below
$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fail++ } }
Check 'GOG manifest written' (Test-Path "$Root\pkg\ucu-manifest-gog.tsv")

foreach ($store in 'steam', 'gog') {
    $retail = if ($store -eq 'gog') { $Gog } else { $Steam }
    $dest = "$Root\install-$store"
    & "$Root\pkg\BattleTechUCUSetup.exe" --retail $retail --dest $dest --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | ForEach-Object { "  setup: $_" }
    Check "$store install exit 0 (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
    Check "$store exe from retail" ((Sha "$dest\BattleTech.exe") -eq (Sha "$Steam\BattleTech.exe"))
    Check "$store same.json" ((Sha (Join-Path $dest $Same)) -eq (Sha (Join-Path $Steam $Same)))
    Check "$store own.bin" ((Sha "$dest\own.bin") -eq (Sha "$Root\player\own.bin"))
    $installed = Join-Path $dest $Bank
    Check "$store sound bank" ((Sha $installed) -eq (Sha (Join-Path $retail $Bank)))
    if (SameDrive $Root $retail) { Check "$store sound bank is a hard link to $store's own file" (LinkedTo $installed (Join-Path $retail $Bank)) }
    else { Check "$store sound bank is a copy ($store is on another drive)" ((Names $installed).Count -eq 1) }
    if ($store -eq 'steam') { Check 'steam sound bank is not linked to GOG' (-not (LinkedTo $installed (Join-Path $Gog $Bank))) }
    $e = Sha (Join-Path $dest $Emblem)
    if ($store -eq 'steam') { Check 'steam emblem is Steam''s' ($e -eq (Sha (Join-Path $Steam $Emblem))) }
    else { Check "gog emblem is Steam's or GOG's ($e)" ($e -eq (Sha (Join-Path $Steam $Emblem)) -or $e -eq (Sha (Join-Path $Gog $Emblem))) }
    foreach ($n in 'Galaxy64.dll', 'GalaxyCSharpGlue.dll') {
        if ($store -eq 'gog') { Check "gog $n" ((Test-Path "$dest\$n") -and (Sha "$dest\$n") -eq (Sha "$Gog\$n")) }
        else { Check "steam has no $n" (-not (Test-Path "$dest\$n")) }
    }
    $dlc = @(Get-ChildItem "$Gog\goggame-*.info" | Where-Object { $_.Name -ne 'goggame-1482783682.info' })
    foreach ($m in $dlc) {
        if ($store -eq 'gog') { Check "gog marker $($m.Name)" ((Test-Path "$dest\$($m.Name)") -and (Sha "$dest\$($m.Name)") -eq (Sha $m.FullName)) }
        else { Check "steam has no $($m.Name)" (-not (Test-Path "$dest\$($m.Name)")) }
    }
    Check "$store has no base-game marker" (-not (Test-Path "$dest\goggame-1482783682.info"))
    $kept = Get-Content -Raw "$dest\ucu-manifest.tsv"
    $want = Get-Content -Raw ("$Root\pkg\" + $(if ($store -eq 'gog') { 'ucu-manifest-gog.tsv' } else { 'ucu-manifest.tsv' }))
    Check "$store keeps its store's manifest" ($kept -eq $want)
    & "$Root\tools\BattleTechUCUSetup.exe" --verify $dest | Out-Null
    Check "$store verify exit 0 (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
}
# A repair re-syncs the markers: a BATTLETECH DLC marker GOG no longer has is removed, another game's is kept.
$stale = "$Root\install-gog\goggame-1111111111.info"; $other = "$Root\install-gog\goggame-2222222222.info"
[IO.File]::WriteAllText($stale, '{ "gameId": "1111111111", "rootGameId": "1482783682" }')
[IO.File]::WriteAllText($other, '{ "gameId": "2222222222", "rootGameId": "1207658924" }')
Remove-Item "$Root\install-gog\$($dlc[0].Name)"
& "$Root\pkg\BattleTechUCUSetup.exe" --retail $Gog --dest "$Root\install-gog" --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | ForEach-Object { "  setup: $_" }
Check "gog repair exit 0 (got $LASTEXITCODE)" ($LASTEXITCODE -eq 0)
Check 'repair removed the stale DLC marker' (-not (Test-Path $stale))
Check "repair kept another game's marker" (Test-Path $other)
if (SameDrive $Root $Gog) { Check 'repair kept the sound bank a hard link to GOG' (LinkedTo (Join-Path "$Root\install-gog" $Bank) (Join-Path $Gog $Bank)) }
Check "repair put back $($dlc[0].Name)" ((Test-Path "$Root\install-gog\$($dlc[0].Name)") -and (Sha "$Root\install-gog\$($dlc[0].Name)") -eq (Sha $dlc[0].FullName))
Remove-Item $other
# Control: a damaged GOG install is caught by Verify install.
[IO.File]::AppendAllText("$Root\install-gog\Galaxy64.dll", 'x')
& "$Root\tools\BattleTechUCUSetup.exe" --verify "$Root\install-gog" | Out-Null
Check "damaged gog verify fails (got $LASTEXITCODE)" ($LASTEXITCODE -ne 0)
if ($fail) { "FAILED $fail" ; exit 1 } else { 'ALL OK'; exit 0 }
