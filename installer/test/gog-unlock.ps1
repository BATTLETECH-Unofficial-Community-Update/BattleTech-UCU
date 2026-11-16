# GOG route of the unlock server (docs/keyserver-design.md, GOG route): packs a small v3 player against the Steam and GOG
# installs, then installs it from GOG with no access code through a local Worker, and checks the route's switches.
# Needs `wrangler dev --local` running from a keyserver copy (keyserver/README.md, Testing locally) whose .dev.vars has
#   MASTER_SECRET=<the hex in -Master>   GOG_FINGERPRINTS=<ReleasePack --fingerprint <GOG install>>[,<others>]
# The Steam install takes a code: a no-code Steam install would start the Steam API as BATTLETECH (637090), which the lease
# guards. The package names an https address that doesn't exist; every request goes to -Url (--unlock-url).
#   -Admin   that copy's admin.ps1 (run with -Local)       -Code   an access code made there with admin.ps1 new-code -Local
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Master,
    [Parameter(Mandatory)][string]$Admin,
    [Parameter(Mandatory)][string]$Code,
    [string]$Url = 'http://127.0.0.1:8787',
    [string]$Steam = 'D:\Steam\steamapps\common\BATTLETECH',
    [string]$Gog = 'F:\Games\BATTLETECH',
    [string]$Bin = 'F:\Unity-Tools\BattleTech-Unity6\tools\release-gate\bin'
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Root) { throw "Root exists: $Root" }
New-Item -ItemType Directory -Force "$Root\tools", "$Root\no-steam", "$Root\player" | Out-Null
Copy-Item "$Bin\*.exe" "$Root\tools"
Copy-Item "$Steam\BattleTech.exe" "$Root\player\BattleTech.exe"
[IO.File]::WriteAllText("$Root\player\steam_appid.txt", '637090')
$b = New-Object byte[] (1MB); (New-Object Random 11).NextBytes($b)
[IO.File]::WriteAllBytes("$Root\player\own.bin", $b)

& "$Root\tools\ReleasePack.exe" --player "$Root\player" --retail $Steam --gog $Gog --key "$Root\key.txt" --out "$Root\pkg" --master-secret $Master --unlock-url 'https://btucu-unlock.invalid' | Tee-Object "$Root\pack.log"
if ($LASTEXITCODE -ne 0) { throw 'pack failed' }
$ErrorActionPreference = 'Continue'   # the installer and wrangler report on stderr; exit codes are checked below
$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fail++ } }
$n = 0
function Install([string]$retail, [string[]]$more) {
    $script:n++
    $dest = "$Root\install-$script:n"
    $out = & "$Root\pkg\BattleTechUCUSetup.exe" --retail $retail --dest $dest --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" --unlock-url $Url @more 2>&1 | ForEach-Object { "$_" }
    $out | ForEach-Object { "  setup: $_" } | Out-Host
    return @{ Code = $LASTEXITCODE; Text = ($out -join "`n"); Dest = $dest }
}

$r = Install $Gog
Check "GOG, no code: installs (exit $($r.Code))" ($r.Code -eq 0 -and (Test-Path "$($r.Dest)\own.bin"))
Check 'GOG install has the Galaxy natives' (Test-Path "$($r.Dest)\Galaxy64.dll")
$r = Install $Steam @('--code', $Code)
Check "Steam with a code: installs, with no Galaxy natives (exit $($r.Code))" ($r.Code -eq 0 -and -not (Test-Path "$($r.Dest)\Galaxy64.dll"))

& $Admin gog-off 'GOG test: route off' -Local | Out-Host
$r = Install $Gog
Check "gog-off: refused with its message (exit $($r.Code))" ($r.Code -ne 0 -and $r.Text -match 'GOG test: route off')
$r = Install $Gog @('--code', $Code)
Check "gog-off: an access code still installs (exit $($r.Code))" ($r.Code -eq 0)
& $Admin gog-on -Local | Out-Host

$release = (Select-String -Path "$Root\pack.log" -Pattern 'unlock server release ([0-9a-f]{32})' | Select-Object -First 1).Matches[0].Groups[1].Value
Check "release id from the pack log ($release)" ($release -match '^[0-9a-f]{32}$')
& $Admin retire $release -Local | Out-Host
$r = Install $Gog
Check "retired release: refused as withdrawn (exit $($r.Code))" ($r.Code -ne 0 -and $r.Text -match 'withdrawn')
& $Admin unretire $release -Local | Out-Host
& $Admin close 'GOG test: closed' -Local | Out-Host
$r = Install $Gog
Check "closed: refused with its message (exit $($r.Code))" ($r.Code -ne 0 -and $r.Text -match 'GOG test: closed')
& $Admin open -Local | Out-Host

# The Release Manager's server check (Unlock.GogRouteProblem): a real proof from this PC's GOG install, a dummy one, and
# a fingerprint the server doesn't hold.
$ErrorActionPreference = 'Stop'   # a missing type must fail here, not pass as a null problem
[void][Reflection.Assembly]::LoadFrom("$Root\tools\ReleaseManager.exe")
$lk = New-Object ReleaseGate.VaultLock
$lk.ReleaseId = [ReleaseGate.Gate]::FromHex($release); $lk.Url = 'https://btucu-unlock.invalid'
$want = [ReleaseGate.Gate]::Share([ReleaseGate.Gate]::FromHex((Get-Content -Raw $Master).Trim()), $lk.ReleaseId)
$real = [ReleaseGate.Unlock]::GogRouteProblem($Url, $lk, [ReleaseGate.Gate]::Fingerprint($Gog), $want)
Check "server check, real proof: passes ($real)" ($null -eq $real)
$dummy = [ReleaseGate.Unlock]::GogRouteProblem($Url, $lk, $null, $want)
Check "server check, dummy proof: passes ($dummy)" ($null -eq $dummy)
$other = [ReleaseGate.Unlock]::GogRouteProblem($Url, $lk, (New-Object byte[] 32), $want)
Check "server check, unknown fingerprint: names GOG_FINGERPRINTS ($other)" ($other -match 'GOG_FINGERPRINTS')
& $Admin gog-off -Local | Out-Null
$off = [ReleaseGate.Unlock]::GogRouteProblem($Url, $lk, $null, $want)
Check "server check, route off: says so ($off)" ($off -match 'off')
& $Admin gog-on -Local | Out-Null
$ErrorActionPreference = 'Continue'

$log = (& $Admin log -Local | Out-String)
Check 'admin log lists gog unlocks' ($log -match 'gog')
& $Admin gog -Local | Out-Host
if ($fail) { "FAILED: $fail check(s)"; exit 1 } else { 'ALL OK'; exit 0 }
