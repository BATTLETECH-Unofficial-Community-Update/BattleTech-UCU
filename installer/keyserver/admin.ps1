# Runs the unlock server's admin tasks against its D1 database through wrangler (../docs/keyserver-design.md).
#   admin.ps1 init                     creates the tables (safe to repeat)
#   admin.ps1 new-code <name>          makes an access code, prints it once, adds it to <Keys>\access-codes.tsv
#   admin.ps1 revoke <name>            that code stops working        admin.ps1 restore <name>   it works again
#   admin.ps1 codes                    every code's name, state and unlock count
#   admin.ps1 log [<name>]             the last 50 unlock attempts, all or one code's
#   admin.ps1 retire <release id>      the server refuses that release    admin.ps1 unretire <release id>
#   admin.ps1 close [<message>]        switches installing off, showing the message to players
#   admin.ps1 open                     switches it back on               admin.ps1 status
#   admin.ps1 steam                    Steam route: accounts seen, borrowed (Family Sharing), blocked, unlocks this week
#   admin.ps1 steam-block <steam id>   that Steam account (64-bit id, 7656...) can't unlock   admin.ps1 steam-unblock <id>
#   admin.ps1 steam-off [<message>]    switches the Steam route off (codes still work)       admin.ps1 steam-on
#   admin.ps1 family allow|deny        whether Family Sharing borrowers may unlock (default allow)
#   admin.ps1 gog                      GOG route: unlocks and refusals this week
#   admin.ps1 gog-off [<message>]      switches the GOG route off (codes still work)         admin.ps1 gog-on
#   admin.ps1 dev-keygen               makes the developer-token signing key: private JWK to <Keys>\dev-signing-key.json
#                                      (then `wrangler secret put DEV_SIGNING_KEY`), public key to <Keys>\dev-public-key.xml
#   admin.ps1 dev-grant <name> [-Dlc all|<pack,pack>] [-Days n]   that code may fetch developer tokens (default all, 30)
#   admin.ps1 dev-ungrant <name>       it may not (tokens already issued live until they expire)
#   admin.ps1 devs                     every developer grant and its last token
# -Local works on the database `npx wrangler dev` uses, for tests. Run from any folder; wrangler runs in this one.
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('init', 'new-code', 'revoke', 'restore', 'codes', 'log', 'retire', 'unretire', 'close', 'open', 'status', 'steam', 'steam-block', 'steam-unblock', 'steam-off', 'steam-on', 'family', 'gog', 'gog-off', 'gog-on', 'dev-keygen', 'dev-grant', 'dev-ungrant', 'devs')][string]$Command,
    [Parameter(Position = 1)][string]$Arg,
    [string]$Dlc = 'all',
    [ValidateRange(1, 90)][int]$Days = 30,
    [switch]$Local,
    [string]$Keys
)
$ErrorActionPreference = 'Stop'
# In the repository the workspace comes from UcuPaths.ps1; in the deployed copy (<workspace>\ReleaseManager\keyserver)
# there is none, and the keys are beside it.
$ucuPaths = Join-Path $PSScriptRoot '..\..\UcuPaths.ps1'
if (-not $Keys) {
    if (Test-Path $ucuPaths) { . $ucuPaths; $Keys = Join-Path $UcuWorkspace 'ReleaseManager\keys' }
    else { $Keys = Join-Path $PSScriptRoot '..\keys' }
}
$here = $PSScriptRoot
$where = if ($Local) { '--local' } else { '--remote' }
# wrangler from this folder's node_modules (npm install), else npx's. Called with $ErrorActionPreference Continue:
# PowerShell 5.1 turns a native tool's stderr lines into errors, and wrangler and npx write notices there.
$wrangler = Join-Path $here 'node_modules\.bin\wrangler.cmd'
if (-not (Test-Path $wrangler)) { $wrangler = 'npx.cmd'; $pre = @('wrangler') } else { $pre = @() }

function Wrangler([string[]]$a) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    Push-Location $here
    $errFile = [IO.Path]::GetTempFileName()
    try { $out = & $wrangler @pre @a 2> $errFile; $code = $LASTEXITCODE }
    finally { Pop-Location; $ErrorActionPreference = $old }
    if ($code -ne 0) {
        # wrangler explains the failure on stdout or stderr; show both, without PowerShell's error-record dressing.
        $err = (Get-Content $errFile | ForEach-Object { "$_" } | Where-Object { $_ -notmatch '^\s*(At |\+ |CategoryInfo|FullyQualifiedErrorId)' }) -join "`n"
        Remove-Item $errFile -ErrorAction SilentlyContinue
        throw "wrangler $($a -join ' ') failed (exit $code):`n$(($out -join "`n").Trim())`n$($err.Trim())"
    }
    Remove-Item $errFile -ErrorAction SilentlyContinue
    return $out
}

function Sql([string]$s) { "'" + $s.Replace("'", "''") + "'" }

function D1([string]$sql) {
    $out = Wrangler @('d1', 'execute', 'btucu-unlock', $where, '--json', '--command', $sql)
    $r = ($out -join "`n") | ConvertFrom-Json
    if ($r -is [array]) { $r = $r[0] }
    return $r.results
}

function Hash([string]$code) {
    $sha = [Security.Cryptography.SHA256]::Create()
    -join ($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($code)) | ForEach-Object { $_.ToString('x2') })
}

function NeedArg([string]$what) { if (-not $Arg) { throw "$Command needs $what" } }

# A Steam account as the server stores it: SHA-256 of "btucu-steam|" + the 64-bit id.
function SteamHash([string]$id) {
    if ($id -notmatch '^7656\d{13}$') { throw "a Steam id is the 17-digit number starting 7656 (steamid.io shows it as steamID64)" }
    Hash "btucu-steam|$id"
}

$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
switch ($Command) {
    'init' {
        Wrangler @('d1', 'execute', 'btucu-unlock', $where, '--file', 'schema.sql') | Out-Null
        'tables ready'
    }
    'new-code' {
        NeedArg 'a name'
        # 20 characters from 32 that can't be confused (no I, O, 0, 1): 100 random bits.
        $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
        $bytes = New-Object byte[] 20
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $code = -join ($bytes | ForEach-Object { $alphabet[$_ % 32] })
        D1 "INSERT INTO codes (hash, name, created) VALUES ($(Sql (Hash $code)), $(Sql $Arg), $now)" | Out-Null
        $shown = $code.Substring(0, 5) + '-' + $code.Substring(5, 5) + '-' + $code.Substring(10, 5) + '-' + $code.Substring(15, 5)
        if (-not $Local) {
            New-Item -ItemType Directory -Force $Keys | Out-Null
            $file = Join-Path $Keys 'access-codes.tsv'
            if (-not (Test-Path $file)) { [IO.File]::WriteAllText($file, "name`tcode`tcreated (UTC)`r`n") }
            [IO.File]::AppendAllText($file, "$Arg`t$shown`t$([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm'))`r`n")
        }
        "$Arg`t$shown"
    }
    'revoke' { NeedArg 'a name'; D1 "UPDATE codes SET revoked = 1 WHERE name = $(Sql $Arg)" | Out-Null; D1 "SELECT name, revoked FROM codes WHERE name = $(Sql $Arg)" | Format-Table }
    'restore' { NeedArg 'a name'; D1 "UPDATE codes SET revoked = 0 WHERE name = $(Sql $Arg)" | Out-Null; D1 "SELECT name, revoked FROM codes WHERE name = $(Sql $Arg)" | Format-Table }
    'codes' {
        D1 "SELECT c.name, datetime(c.created, 'unixepoch') AS created, c.revoked, (SELECT COUNT(*) FROM unlocks u WHERE u.code_hash = c.hash AND u.ok = 1) AS unlocks, (SELECT datetime(MAX(u.at), 'unixepoch') FROM unlocks u WHERE u.code_hash = c.hash AND u.ok = 1) AS last FROM codes c ORDER BY c.created" | Format-Table
    }
    'log' {
        $filter = if ($Arg) { "WHERE c.name = $(Sql $Arg)" } else { '' }
        D1 "SELECT datetime(u.at, 'unixepoch') AS at, COALESCE(c.name, CASE WHEN u.code_hash LIKE 'steam:%' THEN 'steam ' || substr(u.code_hash, 7, 8) WHEN u.code_hash = 'gog' THEN 'gog' ELSE '(unknown code)' END) AS name, u.release, u.ok FROM unlocks u LEFT JOIN codes c ON c.hash = u.code_hash $filter ORDER BY u.at DESC LIMIT 50" | Format-Table
    }
    'retire' { NeedArg 'a release id'; D1 "INSERT OR REPLACE INTO retired (release, at) VALUES ($(Sql $Arg.ToLowerInvariant()), $now)" | Out-Null; "retired $Arg" }
    'unretire' { NeedArg 'a release id'; D1 "DELETE FROM retired WHERE release = $(Sql $Arg.ToLowerInvariant())" | Out-Null; "unretired $Arg" }
    'close' {
        D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('open', 'false')" | Out-Null
        if ($Arg) { D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('closed_message', $(Sql $Arg))" | Out-Null }
        else { D1 "DELETE FROM settings WHERE key = 'closed_message'" | Out-Null }
        'closed'
    }
    'open' { D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('open', 'true')" | Out-Null; 'open' }
    'steam' {
        $week = $now - 7 * 86400
        D1 "SELECT (SELECT COUNT(*) FROM steam) AS accounts, (SELECT COUNT(*) FROM steam WHERE borrowed = 1) AS borrowed, (SELECT COUNT(*) FROM steam WHERE blocked = 1) AS blocked, (SELECT COUNT(*) FROM unlocks WHERE code_hash LIKE 'steam:%' AND ok = 1 AND at > $week) AS unlocks_7d, (SELECT COUNT(*) FROM unlocks WHERE code_hash LIKE 'steam:%' AND ok = 0 AND at > $week) AS refused_7d" | Format-Table
    }
    'steam-block' { NeedArg 'a Steam id'; $h = SteamHash $Arg; D1 "INSERT INTO steam (hash, first, blocked) VALUES ($(Sql $h), $now, 1) ON CONFLICT(hash) DO UPDATE SET blocked = 1" | Out-Null; "blocked steam $($h.Substring(0, 8))" }
    'steam-unblock' { NeedArg 'a Steam id'; $h = SteamHash $Arg; D1 "UPDATE steam SET blocked = 0 WHERE hash = $(Sql $h)" | Out-Null; "unblocked steam $($h.Substring(0, 8))" }
    'steam-off' {
        D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('steam', 'off')" | Out-Null
        if ($Arg) { D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('steam_message', $(Sql $Arg))" | Out-Null }
        else { D1 "DELETE FROM settings WHERE key = 'steam_message'" | Out-Null }
        'steam route off'
    }
    'steam-on' { D1 "DELETE FROM settings WHERE key IN ('steam', 'steam_message')" | Out-Null; 'steam route on' }
    'family' {
        if ($Arg -notin 'allow', 'deny') { throw 'family needs allow or deny' }
        D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('family_sharing', $(Sql $Arg))" | Out-Null
        "family sharing: $Arg"
    }
    'gog' {
        $week = $now - 7 * 86400
        D1 "SELECT (SELECT COUNT(*) FROM unlocks WHERE code_hash = 'gog' AND ok = 1 AND at > $week) AS unlocks_7d, (SELECT COUNT(*) FROM unlocks WHERE code_hash = 'gog' AND ok = 0 AND at > $week) AS refused_7d" | Format-Table
    }
    'gog-off' {
        D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('gog', 'off')" | Out-Null
        if ($Arg) { D1 "INSERT OR REPLACE INTO settings (key, value) VALUES ('gog_message', $(Sql $Arg))" | Out-Null }
        else { D1 "DELETE FROM settings WHERE key = 'gog_message'" | Out-Null }
        'gog route off'
    }
    'gog-on' { D1 "DELETE FROM settings WHERE key IN ('gog', 'gog_message')" | Out-Null; 'gog route on' }
    'status' { D1 "SELECT key, value FROM settings" | Format-Table; D1 "SELECT release, datetime(at, 'unixepoch') AS retired FROM retired" | Format-Table }
    'dev-keygen' {
        $private = Join-Path $Keys 'dev-signing-key.json'
        if (Test-Path $private) { throw "$private exists; move it away first (replacing the key ends every developer token)" }
        $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider 2048
        $p = $rsa.ExportParameters($true)
        $u = { param($b) [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
        $jwk = '{"kty":"RSA","alg":"RS256","ext":true,"key_ops":["sign"],"n":"' + (& $u $p.Modulus) + '","e":"' + (& $u $p.Exponent) + '","d":"' + (& $u $p.D) + '","p":"' + (& $u $p.P) + '","q":"' + (& $u $p.Q) + '","dp":"' + (& $u $p.DP) + '","dq":"' + (& $u $p.DQ) + '","qi":"' + (& $u $p.InverseQ) + '"}'
        New-Item -ItemType Directory -Force $Keys | Out-Null
        [IO.File]::WriteAllText($private, $jwk)
        [IO.File]::WriteAllText((Join-Path $Keys 'dev-public-key.xml'), $rsa.ToXmlString($false))
        "private key: $private  (Get-Content it | npx wrangler secret put DEV_SIGNING_KEY; back it up with the master secret)"
        "public key:  $(Join-Path $Keys 'dev-public-key.xml')  (the player's copy)"
    }
    'dev-grant' {
        NeedArg 'a code name'
        $packs = if ($Dlc -eq 'all') { 'flashpoint,shadowhawkdlc,urbanwarfare,heavymetal' } else { ($Dlc -split '[,\s]+' | Where-Object { $_ }) -join ',' }
        foreach ($pk in $packs -split ',') { if ($pk -and $pk -notin 'flashpoint', 'shadowhawkdlc', 'urbanwarfare', 'heavymetal') { throw "unknown pack $pk (flashpoint, shadowhawkdlc, urbanwarfare, heavymetal or all)" } }
        $row = D1 "SELECT hash FROM codes WHERE name = $(Sql $Arg)"
        if (-not $row) { throw "no code named $Arg (admin.ps1 new-code $Arg first)" }
        D1 "INSERT OR REPLACE INTO developers (hash, dlc, days, granted) VALUES ($(Sql $row[0].hash), $(Sql $packs), $Days, $now)" | Out-Null
        "$Arg may fetch developer tokens: dlc=$packs days=$Days"
    }
    'dev-ungrant' { NeedArg 'a code name'; D1 "DELETE FROM developers WHERE hash IN (SELECT hash FROM codes WHERE name = $(Sql $Arg))" | Out-Null; "$Arg may not fetch developer tokens" }
    'devs' {
        D1 "SELECT c.name, c.revoked, d.dlc, d.days, datetime(d.granted, 'unixepoch') AS granted, (SELECT datetime(MAX(u.at), 'unixepoch') FROM unlocks u WHERE u.code_hash = c.hash AND u.release = 'dev-token' AND u.ok = 1) AS last_token FROM developers d JOIN codes c ON c.hash = d.hash ORDER BY d.granted" | Format-Table
    }
}
