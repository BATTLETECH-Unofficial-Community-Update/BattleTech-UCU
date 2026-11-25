# The destination guard (issue: an install over another BATTLETECH copy). Everything runs on synthetic folders under -Root; no
# real BATTLETECH folder, install or store is read or written. Retail copies are stub exes whose Win32 version resource says what
# Unity built them (2018.4 retail, 6000.x UCU), random data files, and goggame-*.info for a GOG copy.
#   REFUSED  a destination that is, lies inside, or holds a BATTLETECH game this installer didn't install: exit 1, the folder named,
#            nothing written (the folder's tree is identical before and after), whether the path is typed with another case, a
#            trailing slash, an 8.3 name, a junction or a symlink.
#   ACCEPTED a fresh or empty folder, a folder with no game, and every shape of an install of ours: with its record, with no
#            record (a restored backup), with a damaged record, killed during its first install, a GOG one, an older exe.
# -Setup is the installer under test; -Bin holds the ReleasePack.exe that packs the set (default: -Setup's folder). Run it on a
# build before the fix for the red result: the REFUSED legs fail there and the ACCEPTED legs pass.
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Setup,
    [string]$OtherDir = $env:TEMP    # a folder on another drive than -Root, for the junction-to-another-drive leg
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Root) { throw "Root exists: $Root" }
$Root = [IO.Path]::GetFullPath($Root)
$Bin = Split-Path ([IO.Path]::GetFullPath($Setup))
. (Join-Path $PSScriptRoot 'synth.ps1')
New-Item -ItemType Directory -Force "$Root\no-steam", "$Root\ok", "$Root\v" | Out-Null
$fail = 0; $notRun = @()
function Check([string]$what, [bool]$ok) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fail++ } }
function Rand([string]$path, [int]$size, [int]$seed) {
    $b = New-Object byte[] $size; (New-Object Random $seed).NextBytes($b)
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllBytes($path, $b)
}
function Tree([string]$d) {
    if (-not (Test-Path -LiteralPath $d)) { return 'absent' }
    (Get-ChildItem -LiteralPath $d -Recurse -File -Force | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($d.Length) + ' ' + $_.Length + ' ' + (Get-FileHash -LiteralPath $_.FullName).Hash.Substring(0, 16) }) -join "`n"
}

# Stub exes: the version resource is what the installer reads (Unity stamps its own version into BattleTech.exe).
$n = 0
function New-Stub([string]$path, [string]$version) {
    $script:n++
    Add-Type -OutputAssembly $path -OutputType ConsoleApplication -TypeDefinition ('using System.Reflection; [assembly: AssemblyFileVersion("{0}")] [assembly: AssemblyVersion("1.0.0.0")] public static class Stub{1} {{ public static void Main() {{ }} }}' -f $version, $script:n)
}
New-Stub "$Root\stub-retail.exe" '2018.4.2.14089'
New-Stub "$Root\stub-ucu.exe" '6000.6.9.9192'
New-Stub "$Root\stub-ucu-old.exe" '6000.0.30.1'

# The two retail installs (random bytes), a GOG DLC marker, a player and an unlocked package that carries both stores.
New-SynthInstalls "$Root\src"
[IO.File]::WriteAllText("$Root\src\gog\goggame-1314028793.info", '{"rootGameId":"1482783682","gameId":"1314028793"}')
$player = "$Root\player"
New-Item -ItemType Directory -Force "$player\BattleTech_Data" | Out-Null
Copy-Item "$Root\stub-ucu.exe" "$player\BattleTech.exe"
Copy-Item "$Root\src\steam\BattleTech_Data\resources.assets" "$player\BattleTech_Data\"
Rand "$player\BattleTech_Data\own.bin" 1000000 9
[IO.File]::WriteAllText("$player\steam_appid.txt", '637090')
& "$Bin\ReleasePack.exe" --player $player --retail "$Root\src\steam" --gog "$Root\src\gog" --key "$Root\key.txt" --out "$Root\pkg" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'pack failed' }
Copy-Item $Setup "$Root\pkg\setup-under-test.exe" -Force
$ErrorActionPreference = 'Continue'   # the installer reports on stderr; exit codes are checked below

function Run([string]$retail, [string]$dest) {
    # stderr lines come back as error records whose message is the line itself; Out-String would wrap them at the console width
    $lines = & "$Root\pkg\setup-under-test.exe" --retail $retail --dest $dest --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 |
        ForEach-Object { if ($_ -is [Management.Automation.ErrorRecord]) { $_.Exception.Message } else { "$_" } }
    $script:code = $LASTEXITCODE
    $script:out = ($lines -join "`n").Trim()
}

# Retail copies the destination must never take: a second Steam copy, a GOG copy, a Steam copy beside it, one with no exe, and one
# whose exe carries no version resource (cannot be told from retail, so it counts as one).
function New-Copy([string]$name, [string]$from, [string]$exe) {
    Copy-Item -Recurse "$Root\src\$from" "$Root\v\$name"
    if ($exe) { Copy-Item $exe "$Root\v\$name\BattleTech.exe" -Force }
}
New-Copy 'steam-copy' 'steam' "$Root\stub-retail.exe"
New-Copy 'steam-copy2' 'steam' "$Root\stub-retail.exe"
New-Copy 'gog-copy' 'gog' "$Root\stub-retail.exe"
New-Copy 'no-exe' 'steam' $null; Remove-Item "$Root\v\no-exe\BattleTech.exe"
New-Copy 'bare-exe' 'steam' $null          # BattleTech.exe of random bytes: no version to read
New-Item -ItemType Directory "$Root\v\exe-only" | Out-Null; Copy-Item "$Root\stub-retail.exe" "$Root\v\exe-only\BattleTech.exe"
$before = @{}; foreach ($d in 'steam-copy', 'steam-copy2', 'gog-copy', 'no-exe', 'bare-exe', 'exe-only') { $before[$d] = Tree "$Root\v\$d" }
New-Item -ItemType Junction -Path "$Root\ok\to-gog" -Target "$Root\v\gog-copy" | Out-Null
$retailBefore = (Tree "$Root\src\steam") + (Tree "$Root\src\gog")

$steam = "$Root\src\steam"; $gog = "$Root\src\gog"
function Refused([string]$what, [string]$retail, [string]$dest, [string]$guarded, [string]$names) {
    $t = Tree "$Root\v\$guarded"
    Run $retail $dest
    Check "$what`: refused, exit 1 (got $code)" ($code -eq 1)
    Check "$what`: names $names ($($out -replace '\s+', ' '))" ($out -match [regex]::Escape($names))
    $flat = $out -replace '\s+', ' '
    Check "$what`: advises moving it away, never deleting" ($flat -match 'move it away first' -and $flat -notmatch "(?<!don't )delete")
    Check "$what`: $guarded is as it was" ((Tree "$Root\v\$guarded") -eq $t -and $t -eq $before[$guarded])
}
# ACCEPTED
function Verifies([string]$d, [string]$what) { & "$Root\pkg\setup-under-test.exe" --verify $d *> $null; Check "$what verifies (exit $LASTEXITCODE)" ($LASTEXITCODE -eq 0) }
function Installed([string]$what, [string]$retail, [string]$dest) {
    Run $retail $dest
    Check "$what`: installs (exit $code)" ($code -eq 0)
    if ($code -ne 0) { "     $($out -replace '\s+', ' ')" }
}
Installed 'a fresh folder'                   $steam "$Root\ok\fresh"
Verifies "$Root\ok\fresh" '  fresh install'
New-Item -ItemType Directory "$Root\ok\empty" | Out-Null
Installed 'an existing empty folder'         $steam "$Root\ok\empty"
New-Item -ItemType Directory "$Root\ok\notes\Mods\SomeMod" -Force | Out-Null
[IO.File]::WriteAllText("$Root\ok\notes\readme.txt", 'mine'); [IO.File]::WriteAllText("$Root\ok\notes\Mods\SomeMod\mod.json", '{}')
Installed 'a non-empty folder with no game (mods present)' $steam "$Root\ok\notes"
Check '  its own files are still there' ((Get-Content "$Root\ok\notes\readme.txt") -eq 'mine' -and (Test-Path "$Root\ok\notes\Mods\SomeMod\mod.json"))
# A loose retail exe above the destination, with no data folder, is no game to write over (a stray BattleTech.exe in a parent).
New-Item -ItemType Directory "$Root\ok\loose" | Out-Null; Copy-Item "$Root\stub-retail.exe" "$Root\ok\loose\BattleTech.exe"
Installed 'a folder below a loose retail exe with no data folder' $steam "$Root\ok\loose\ucu"
Installed 'an update of an install with its record' $steam "$Root\ok\fresh"
Verifies "$Root\ok\fresh" '  the update left the install'
function Shape([string]$name, [scriptblock]$make, [string]$retail = $steam) {
    Copy-Item -Recurse "$Root\ok\fresh" "$Root\ok\$name"
    & $make "$Root\ok\$name"
    Installed "an update with $name" $retail "$Root\ok\$name"
}
Shape 'no record (a restored backup)'  { param($d) Remove-Item "$d\.btu6-install.tsv" }
Shape 'a damaged record'               { param($d) [IO.File]::WriteAllBytes("$d\.btu6-install.tsv", [byte[]](1..200 | ForEach-Object { Get-Random -Maximum 256 })) }
Shape 'an empty record'                { param($d) Set-Content "$d\.btu6-install.tsv" '' }
Shape 'an older Unity 6 exe, no record' { param($d) Remove-Item "$d\.btu6-install.tsv"; Copy-Item "$Root\stub-ucu-old.exe" "$d\BattleTech.exe" -Force }
Shape 'the unfinished marker, no record, no exe (killed first install)' { param($d) Remove-Item "$d\.btu6-install.tsv", "$d\BattleTech.exe"; [IO.File]::WriteAllText("$d\.btu6-updating", 'x') }
Shape 'a record and the exe gone'      { param($d) Remove-Item "$d\BattleTech.exe" }
New-Item -ItemType Directory "$Root\ok\killed\BattleTech_Data" -Force | Out-Null
[IO.File]::WriteAllText("$Root\ok\killed\.btu6-updating", 'x'); Rand "$Root\ok\killed\BattleTech_Data\resources.assets" 5000 3
Installed 'a first install killed before its record (marker and part of the data)' $steam "$Root\ok\killed"
# A GOG UCU install: its own record, then without it; the DLC marker stays and no base marker is copied.
Installed 'a fresh GOG-sourced folder'       $gog "$Root\ok\gogucu"
Check '  GOG install holds the DLC marker, not the base one' ((Test-Path "$Root\ok\gogucu\goggame-1314028793.info") -and -not (Test-Path "$Root\ok\gogucu\goggame-1482783682.info"))
Installed 'an update of a GOG install with its record' $gog "$Root\ok\gogucu"
Remove-Item "$Root\ok\gogucu\.btu6-install.tsv"
Installed 'an update of a GOG install with no record' $gog "$Root\ok\gogucu"
# A parent that only holds a game is not written over: our files go beside it, the game inside stays as it was.
New-Item -ItemType Directory "$Root\parent" | Out-Null
Copy-Item -Recurse "$Root\src\steam" "$Root\parent\steam-inside"; Copy-Item "$Root\stub-retail.exe" "$Root\parent\steam-inside\BattleTech.exe" -Force
$inside = Tree "$Root\parent\steam-inside"
Installed 'a parent folder that only holds a game' $steam "$Root\parent"
Check '  the game inside is as it was' ((Tree "$Root\parent\steam-inside") -eq $inside)
# Ours is never refused for what lies around it: an install (its record, or only the unfinished marker) inside a retail copy, and an
# install with a backup of the game inside it. Each updates, verifies, and leaves the game around or inside it as it was.
New-Item -ItemType Directory "$Root\w" | Out-Null
Copy-Item -Recurse "$Root\src\steam" "$Root\w\host-copy"; Copy-Item "$Root\stub-retail.exe" "$Root\w\host-copy\BattleTech.exe" -Force
$hostGame = (Tree "$Root\w\host-copy\BattleTech_Data") + (Get-FileHash "$Root\w\host-copy\BattleTech.exe").Hash
Copy-Item -Recurse "$Root\ok\fresh" "$Root\w\host-copy\ucu"
New-Item -ItemType Directory "$Root\w\host-copy\ucu-killed\BattleTech_Data" -Force | Out-Null
[IO.File]::WriteAllText("$Root\w\host-copy\ucu-killed\.btu6-updating", 'x')
Installed 'our install (record) inside a retail copy' $steam "$Root\w\host-copy\ucu"
Verifies "$Root\w\host-copy\ucu" '  that install'
Installed 'our killed first install (marker only) inside a retail copy' $steam "$Root\w\host-copy\ucu-killed"
Check '  the retail copy around them is as it was' (((Tree "$Root\w\host-copy\BattleTech_Data") + (Get-FileHash "$Root\w\host-copy\BattleTech.exe").Hash) -eq $hostGame)
Copy-Item -Recurse "$Root\ok\fresh" "$Root\ok\withbackup"; Copy-Item -Recurse "$Root\src\gog" "$Root\ok\withbackup\backup-gog"
$backup = Tree "$Root\ok\withbackup\backup-gog"
Installed 'our install with a backup of a game inside it' $steam "$Root\ok\withbackup"
Verifies "$Root\ok\withbackup" '  that install'
Check '  the backup inside is as it was' ((Tree "$Root\ok\withbackup\backup-gog") -eq $backup)
# Drives: the drive and the free space stay on the typed path, so a subst letter, a mapped drive and a junction to another drive
# install as they always did (the real path of each is another one: \\server\share\..., or the other drive's).
function FreeLetter([string]$from) { [char[]]$from | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1 }
$letter = FreeLetter 'RSTUVWXYZ'
subst "$($letter):" "$Root\ok" | Out-Null
try {
    Installed "a subst drive ($($letter):) onto a folder" $steam "$($letter):\subst-fresh"
    Check '  it landed in the folder behind the letter' (Test-Path "$Root\ok\subst-fresh\BattleTech.exe")
}
finally { subst "$($letter):" /d | Out-Null }
$other = Join-Path $OtherDir ('dg-other-drive-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $other | Out-Null
New-Item -ItemType Junction -Path "$Root\ok\to-other-drive" -Target $other | Out-Null
Run $steam "$Root\ok\to-other-drive\inst"
# Where it lands depends on the child being allowed to write on the other drive (the session sandbox here says "Can't write to that
# folder", and HEAD's installer says the same); what this leg scores is that the destination check passes without a crash or a refusal.
Check "a junction to a folder on another drive: the destination check passes (exit $code)" ($out -notmatch 'Unhandled|Exception|installer didn.t install|needed on|valid folder|isn.t available')
if ($code -eq 0) { Check '  it landed on the other drive' (Test-Path "$other\inst\BattleTech.exe") }
else { $notRun += "junction to another drive: the install itself (the child can't write there: $($out -replace '\s+', ' '))" }
cmd /c rmdir "$Root\ok\to-other-drive"; cmd /c rd /s /q $other
$net = FreeLetter 'YXWVUTSR'
$tmpRoot = [IO.Path]::GetPathRoot($OtherDir)
net use "$($net):" "\\localhost\$($tmpRoot.Substring(0, 1))`$" /persistent:no *> $null
if ($LASTEXITCODE -eq 0) {
    $mapped = Join-Path $OtherDir ('dg-mapped-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        Run $steam ("$($net):" + $mapped.Substring(2))
        # A mapping belongs to one logon session: a child that can't see it says so, on HEAD's installer too, and nothing is scored.
        if ($out -match 'Drive .* isn.t available') { $script:notRun += "mapped network drive (this session's child processes can't see the mapping; HEAD's installer answers the same)" }
        else { Check "a mapped network drive ($($net):): installs (exit $code)" ($code -eq 0); Check '  it landed behind the share' (Test-Path "$mapped\BattleTech.exe") }
    }
    finally { net use "$($net):" /delete /y *> $null; if (Test-Path $mapped) { cmd /c rd /s /q $mapped } }
}
else { $notRun += 'mapped network drive (\\localhost admin share refused here)' }
Check 'retail source folders untouched by the installs' (((Tree $steam) + (Tree $gog)) -eq $retailBefore)
Check 'every retail copy is as it was' ((@('steam-copy', 'steam-copy2', 'gog-copy', 'no-exe', 'bare-exe') | Where-Object { (Tree "$Root\v\$_") -ne $before[$_] }).Count -eq 0)

# THE WINDOW: the Install to box shows the refusal and keeps Install disabled, with no write and nothing asked. The window reads the
# user's own launcher.ini (Windows' known folder, not %APPDATA%), so this leg only types into the two boxes and proves that file
# is unchanged. Typing is by window message (win.ps1), so it takes no focus and sends no physical input.
function WindowLeg {
. (Join-Path $PSScriptRoot 'win.ps1')
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);' -Name Tab -Namespace Dg
$ini = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'BattleTechUCU\launcher.ini'
function IniState { if (Test-Path $ini) { (Get-FileHash $ini).Hash + (Get-Item $ini).LastWriteTimeUtc.Ticks } else { 'none' } }
$iniBefore = IniState
$treeBefore = Tree "$Root\v\gog-copy"
$wp = [Win]::Start("$Root\pkg\setup-under-test.exe", '--no-detect', "$Root\pkg")
try {
    Start-Sleep 5
    $top = [Win]::Tops($wp) | Where-Object { [Win]::Class($_) -like 'WindowsForms10.Window*' } | Select-Object -First 1
    $tabs = [Win]::Children($top) | Where-Object { [Win]::Class($_) -like '*SysTabControl32*' } | Select-Object -First 1
    [Dg.Tab]::SendMessage([IntPtr]$tabs, 0x1330, [IntPtr]1, [IntPtr]::Zero) | Out-Null   # Install / Update
    Start-Sleep 2
    function Kids { [Win]::Children($top) }
    function Edit([int]$n) { @(Kids | Where-Object { [Win]::Class($_) -like '*EDIT*' })[$n] }
    function Texts { (Kids | ForEach-Object { [Win]::Text($_) }) -join "`n" }
    function InstallButton { Kids | Where-Object { [Win]::Class($_) -like '*BUTTON*' -and [Win]::Text($_) -eq 'Install' } | Select-Object -First 1 }
    [Win]::SetText((Edit 0), "$Root\src\steam")
    $found = WaitFor { (Texts) -match 'BATTLETECH found\.' } 60
    Check 'window: the BATTLETECH folder is accepted' $found
    foreach ($case in @(@('another BATTLETECH copy', "$Root\v\gog-copy"), @('inside a copy', "$Root\v\steam-copy\ucu"), @('a junction to a copy', "$Root\ok\to-gog"))) {
        [Win]::SetText((Edit 1), $case[1])
        $shown = WaitFor { (Texts) -match "didn.t install" } 60
        Check "window, $($case[0]): the box names the refusal" $shown
        Check "window, $($case[0]): Install stays disabled" (-not [Win]::IsWindowEnabled((InstallButton)))
    }
    [Win]::SetText((Edit 1), "$Root\ok\window-fresh")
    $ready = WaitFor { [Win]::IsWindowEnabled((InstallButton)) } 60
    Check 'window, control, a fresh folder: Install is enabled' $ready
    Check 'window, control, a fresh folder: no refusal showing' ((Texts) -notmatch "didn.t install")
}
finally { Stop-Process -Id $wp -Force -ErrorAction SilentlyContinue }
Check 'window: nothing was written to the copy' ((Tree "$Root\v\gog-copy") -eq $treeBefore)
Check 'window: the user''s launcher.ini is unchanged' ((IniState) -eq $iniBefore)
}

function RefusedLegs {
Refused 'Steam over a GOG copy'      $steam "$Root\v\gog-copy" 'gog-copy' 'gog-copy'
Refused 'GOG over a Steam copy'      $gog "$Root\v\steam-copy" 'steam-copy' 'steam-copy'
Refused 'a second Steam copy'        $steam "$Root\v\steam-copy2" 'steam-copy2' 'steam-copy2'
Refused 'a folder inside a copy'     $steam "$Root\v\steam-copy\ucu" 'steam-copy' 'steam-copy'
Refused 'a folder below a copy'      $steam "$Root\v\steam-copy2\BattleTech_Data\x" 'steam-copy2' 'steam-copy2'
Refused 'a copy with no exe'         $steam "$Root\v\no-exe" 'no-exe' 'no-exe'
Refused 'a copy whose exe has no version (doubtful)' $steam "$Root\v\bare-exe" 'bare-exe' 'bare-exe'
Refused 'a folder with only the exe' $steam "$Root\v\exe-only" 'exe-only' 'exe-only'
Refused 'case differs'               $steam "$Root\v\GOG-COPY" 'gog-copy' 'gog-copy'
Refused 'trailing slash'             $steam "$Root\v\gog-copy\" 'gog-copy' 'gog-copy'
Refused 'dots in the path'           $steam "$Root\v\steam-copy2\..\gog-copy\." 'gog-copy' 'gog-copy'
$short = (New-Object -ComObject Scripting.FileSystemObject).GetFolder("$Root\v\gog-copy").ShortPath
if ($short -and $short -ne "$Root\v\gog-copy" -and $short -match '~') { Refused "8.3 name ($short)" $steam $short 'gog-copy' 'gog-copy' }
else { $script:notRun += '8.3 name (the volume makes no short names)' }
Refused 'a junction to it'           $steam "$Root\ok\to-gog" 'gog-copy' 'gog-copy'
Refused 'below a junction to it'     $steam "$Root\ok\to-gog\ucu" 'gog-copy' 'gog-copy'
try { New-Item -ItemType SymbolicLink -Path "$Root\ok\sym-steam" -Target "$Root\v\steam-copy" -ErrorAction Stop | Out-Null; Refused 'a symlink to it' $steam "$Root\ok\sym-steam" 'steam-copy' 'steam-copy' }
catch { $script:notRun += "symlink to a copy ($($_.Exception.Message))" }
# The same junction reached through a subst drive: the real path still gives it away.
$letter = FreeLetter 'RSTUVWXYZ'
subst "$($letter):" "$Root\ok" | Out-Null
try { Refused 'a junction to a copy, through a subst drive' $steam "$($letter):\to-gog" 'gog-copy' 'gog-copy' }
finally { subst "$($letter):" /d | Out-Null }
# The control that existed before: the --retail folder itself.
Run $steam $steam
Check "control, --dest is the --retail folder: refused (exit $code)" ($code -eq 1 -and $out -match 'BATTLETECH game that this installer didn.t install|outside your BATTLETECH folder')
Run $steam "$steam\sub"
Check "control, below the --retail folder: refused (exit $code)" ($code -eq 1)
$j = "$Root\ok\to-steam-retail"; New-Item -ItemType Junction -Path $j -Target $steam | Out-Null
Run $steam $j
Check "a junction to the --retail folder: refused (exit $code)" ($code -eq 1)

}
WindowLeg      # before the refusals: on a build without the guard they write into the synthetic retail, which the window then won't accept
RefusedLegs
$retailAfter = (Tree $steam) + (Tree $gog)
Check 'retail source folders untouched by the refusals' ($retailAfter -eq $retailBefore)
Check 'every retail copy is as it was' ((@('steam-copy', 'steam-copy2', 'gog-copy', 'no-exe', 'bare-exe', 'exe-only') | Where-Object { (Tree "$Root\v\$_") -ne $before[$_] }).Count -eq 0)

foreach ($x in $notRun) { "NOT RUN $x" }
if ($fail) { "FAILED $fail"; exit 1 } else { 'ALL OK'; exit 0 }
