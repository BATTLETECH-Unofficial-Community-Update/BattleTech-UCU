# The launcher's offer to replace itself (Remote.NewerLauncher, Launcher.ReplaceSelf), driven in its window by messages.
# Builds launchers of three file versions whose release source is a local stand-in (srv.py), puts one in a synthetic install
# as the launcher copy, and serves another. Those builds keep launcher.ini and the saved code under -Root; the user's own are
# neither read nor written (checked).
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Retail,
    [int]$Port = 18766
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\UcuPaths.ps1')
. (Join-Path $PSScriptRoot 'win.ps1')
if (-not $Retail) { $Retail = Get-UcuPath retail_install -Required }
if (Test-Path $Root) { throw "Root exists: $Root" }
& (Join-Path $PSScriptRoot 'make.ps1') -Root $Root -Retail $Retail | Out-Null
New-Item -ItemType Directory -Force "$Root\no-steam", "$Root\serve" | Out-Null
$gate = Split-Path $PSScriptRoot
$url = "http://127.0.0.1:$Port/release.json"
$fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fail++ } }
function Hash([string]$p) { (Get-FileHash -LiteralPath $p).Hash }
function Build([string]$version) {
    $d = "$Root\src-$version"
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item "$gate\src" $d -Recurse
    Copy-Item "$gate\build.ps1" $d
    $f = "$d\src\AssemblyInfo.cs"
    [IO.File]::WriteAllText($f, ([IO.File]::ReadAllText($f) -replace 'AssemblyFileVersion\("[\d.]+"\)', "AssemblyFileVersion(`"$version`")"))
    $f = "$d\src\Remote.cs"
    $t = [IO.File]::ReadAllText($f)
    $n = $t -replace 'DefaultSource = "[^"]+"', "DefaultSource = `"$url`""
    if ($n -eq $t) { throw 'DefaultSource not found' }
    [IO.File]::WriteAllText($f, $n)
    foreach ($s in 'Launcher.cs', 'Unlock.cs') {
        $f = "$d\src\$s"
        $t = [IO.File]::ReadAllText($f)
        $n = $t.Replace('Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)', "@`"$Root\profile`"")
        if ($n -eq $t) { throw "the settings folder wasn't found in $s" }
        [IO.File]::WriteAllText($f, $n)
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$d\build.ps1" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build $version failed" }
    "$d\bin\BattleTechUCUSetup.exe"
}
$v1 = Build '9.0.1.0'; $v2 = Build '9.0.2.0'; $v0 = Build '9.0.0.0'

$inst = "$Root\inst"; $exe = "$inst\BattleTech UCU Launcher.exe"
Copy-Item $v1 "$Root\pkg2\setup.exe"
& "$Root\pkg2\setup.exe" --retail $Retail --dest $inst --no-mods --steam-root "$Root\no-steam" --backup-root "$Root\no-steam" 2>&1 | Out-Null
if (-not (Test-Path "$inst\.btu6-install.tsv")) { throw 'install failed' }

$ini = "$env:APPDATA\BattleTechUCU\launcher.ini"
function IniState { if (Test-Path $ini) { (Hash $ini) + (Get-Item $ini).LastWriteTimeUtc.Ticks } else { 'none' } }
$iniBefore = IniState

$srv = $null
function Serve([string]$installer, [string]$digest) {
    Unserve
    Copy-Item "$Root\serve2\*" "$Root\serve" -Force
    Copy-Item $installer "$Root\serve\BattleTechUCUSetup.exe" -Force
    $script:log = "$Root\srv-$([guid]::NewGuid().ToString('N').Substring(0, 6)).log"
    $a = '-3', "`"$PSScriptRoot\srv.py`"", '--dir', "`"$Root\serve`"", '--log', "`"$log`"", '--port', "$Port"
    if ($digest) {
        $size = (Get-Item "$Root\serve\BattleTechUCUSetup.exe").Length
        [IO.File]::WriteAllText("$Root\release.json", "{`"tag_name`":`"test-release`",`"assets`":[{`"name`":`"BattleTechUCUSetup.exe`",`"size`":$size,`"browser_download_url`":`"http://127.0.0.1:$Port/dl/BattleTechUCUSetup.exe`"$digest}]}")
        $a += '--release', "`"$Root\release.json`""
    }
    $script:srv = Start-Process py -ArgumentList $a -PassThru -WindowStyle Hidden
    Start-Sleep 2
}
function Unserve {
    Get-CimInstance Win32_Process -Filter "Name='python.exe' or Name='py.exe'" | Where-Object { $_.CommandLine -like '*srv.py*' -and $_.CommandLine -like "*--port $Port*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
function Running { @(Get-Process | Where-Object { try { $_.Path -and $_.Path.StartsWith($inst, 'OrdinalIgnoreCase') } catch { $false } }) }
function StopAll { Running | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }; Start-Sleep 1 }
function Start1([string]$launcher, [string]$extra = '') {
    StopAll
    Remove-Item "$exe.old", "$exe.new" -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $launcher $exe -Force
    $script:p = [Win]::Start($exe, "--no-detect --steam-root `"$Root\no-steam`" --backup-root `"$Root\no-steam`" $extra", $inst)
}
function Dialog([int]$id, [int]$seconds) {
    $script:dlg = $null
    $null = WaitFor { $script:dlg = [Win]::Tops($id) | Where-Object { [Win]::Class($_) -eq '#32770' } | Select-Object -First 1; $script:dlg } $seconds
    $script:dlg
}
function Press([IntPtr]$d, [string]$name) { [Win]::Click(([Win]::Children($d) | Where-Object { [Win]::Text($_) -eq "&$name" } | Select-Object -First 1)) }
function Kid([int]$id, [string]$text) { [Win]::Tops($id) | ForEach-Object { [Win]::Children($_) } | Where-Object { [Win]::Text($_) -like $text } | Select-Object -First 1 }
# The button's accessible default action: a posted BM_CLICK does nothing while another window covers the button.
Add-Type -TypeDefinition @'
using System; using System.Reflection; using System.Runtime.InteropServices;
public static class Acc {
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IDispatch)] out object acc);
    public static void Do(IntPtr h) {
        var iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71"); object a;
        Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(h, 0xFFFFFFFC, ref iid, out a));
        a.GetType().InvokeMember("accDoDefaultAction", BindingFlags.InvokeMethod, null, a, new object[] { 0 });
    }
}
'@
function Updated { (Hash $exe) -eq (Hash $v2) }
function Fetched { [bool](Select-String -Path $log -Pattern 'BattleTechUCUSetup' -Quiet) }

try {
    Serve $v2
    Start1 $v1
    $d = Dialog $p 25
    Check 'Home: a newer launcher is offered' ([bool]$d -and [bool]([Win]::Children($d) | Where-Object { [Win]::Text($_) -match 'new version of this launcher' }))
    if ($d) { Press $d 'Yes' }
    $null = WaitFor { (Updated) -and @(Running).Count -eq 1 -and (Running)[0].Id -ne $p } 20
    Check 'Yes: the launcher is the new one' (Updated)
    Check 'Yes: the old one closed and one new one runs' (@(Running).Count -eq 1 -and (Running)[0].Id -ne $p)
    Check 'the replaced launcher is kept as .old' ((Test-Path "$exe.old") -and (Hash "$exe.old") -eq (Hash $v1))
    $q = (Running)[0].Id
    $line = (Get-CimInstance Win32_Process -Filter "ProcessId=$q").CommandLine
    Check 'the restart keeps the arguments' ($line -like '*--no-detect*' -and $line -like '*--backup-root*no-steam*')
    Check 'the new launcher offers nothing (no loop)' (-not (Dialog $q 10))

    Serve $v2
    Start1 $v1
    $d = Dialog $p 25
    if ($d) { Press $d 'No' }
    Start-Sleep 2
    $b = Kid $p 'Update launcher'
    Check 'No: the launcher stays, with a button' ([bool]$d -and (Hash $exe) -eq (Hash $v1) -and [bool]$b -and [Win]::Visible($b))
    if ($b) { [Acc]::Do($b) }
    $null = WaitFor { (Updated) -and @(Running).Count -eq 1 -and (Running)[0].Id -ne $p } 20
    Check 'the button updates and restarts' ((Updated) -and @(Running).Count -eq 1 -and (Running)[0].Id -ne $p)

    Serve $v2
    Start1 $v1 "--remote $url"
    $d = Dialog $p 25
    Check 'reading a release online offers it too' ([bool]$d -and [bool]([Win]::Children($d) | Where-Object { [Win]::Text($_) -match 'new version of this launcher' }))

    Serve $v1
    Start1 $v1
    Check 'the published launcher itself: nothing offered' (-not (Dialog $p 12))
    Check 'and nothing downloaded' ((Test-Path $log) -and -not (Fetched))

    Serve $v0
    Start1 $v1
    Check 'an older published launcher: downloaded, not offered' (-not (Dialog $p 12) -and (Fetched) -and (Hash $exe) -eq (Hash $v1))

    Serve $v2 ",`"digest`":`"sha256:$('0' * 64)`""
    Start1 $v1
    Check 'a download that fails its check: not offered' (-not (Dialog $p 15) -and (Fetched) -and (Hash $exe) -eq (Hash $v1))

    Serve $v2 ' '
    Start1 $v1
    Check 'a release without a digest: not offered, not downloaded' (-not (Dialog $p 12) -and (Test-Path $log) -and -not (Fetched))

    Serve $v2
    Start1 $v1
    $d = Dialog $p 25
    New-Item -ItemType Directory "$exe.new" | Out-Null
    if ($d) { Press $d 'Yes' }
    Start-Sleep 3
    Check 'a folder that cannot be written: said, launcher kept and running' ([bool]$d -and [bool](Kid $p "The launcher couldn't be updated*") -and (Hash $exe) -eq (Hash $v1) -and @(Running).Count -eq 1)
    Remove-Item "$exe.new"

    Start1 $v1
    $d = Dialog $p 25
    $lock = $null
    Copy-Item $v0 "$exe.old" -Force
    $lock = [IO.File]::Open("$exe.old", 'Open', 'Read', 'Read')
    if ($d) { Press $d 'Yes' }
    Start-Sleep 3
    $lock.Dispose()
    Check 'an .old in use: said, launcher kept, no .new left' ([bool]$d -and [bool](Kid $p "The launcher couldn't be updated*") -and (Hash $exe) -eq (Hash $v1) -and -not (Test-Path "$exe.new"))

    # A newer launcher that can't be started (an ARM64 mark on the x64 exe): what is said, and what is left to go back to.
    $dead = "$Root\dead.exe"; $bytes = [IO.File]::ReadAllBytes($v2); $pe = [BitConverter]::ToInt32($bytes, 60)
    $bytes[$pe + 4] = 0x64; $bytes[$pe + 5] = 0xAA; [IO.File]::WriteAllBytes($dead, $bytes)
    Serve $dead
    Start1 $v1
    $d = Dialog $p 25
    if ($d) { Press $d 'Yes' }
    Start-Sleep 4
    Check 'a new launcher that cannot start: said, the old one keeps running' ([bool]$d -and [bool](Kid $p "The launcher couldn't be updated*") -and @(Running).Count -eq 1 -and (Running)[0].Id -eq $p)
    "note the launcher file is now $(if ((Hash $exe) -eq (Hash $dead)) { 'the one that cannot start' } elseif ((Hash $exe) -eq (Hash $v1)) { 'the old one' } else { 'something else' }), .old is $(if ((Test-Path "$exe.old") -and (Hash "$exe.old") -eq (Hash $v1)) { 'the working one' } else { 'not the working one' })"
} finally {
    StopAll
    Unserve
}
Check 'the user''s launcher.ini is unchanged' ((IniState) -eq $iniBefore)
if ($fail) { "FAILED $fail"; exit 1 } else { 'ALL OK'; exit 0 }
