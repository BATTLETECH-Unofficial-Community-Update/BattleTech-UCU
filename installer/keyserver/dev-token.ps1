# Fetches or checks a developer token (README.md → Developer tokens; the maintainer, 2026-11-20).
#   dev-token.ps1 -Code <access code> -Url <unlock server> [-Out <file>]   asks the server, writes the token (default
#                                                                          ucu-dev-token.txt here) and prints its payload
#   dev-token.ps1 -Check <file> -PublicKey <dev-public-key.xml>            checks a token the way the player does
# The player reads the token from ucu-dev-token.txt beside BattleTech.exe. Keep it out of git and out of packages.
param(
    [string]$Code,
    [string]$Url,
    [string]$Out = (Join-Path (Get-Location) 'ucu-dev-token.txt'),
    [string]$Check,
    [string]$PublicKey
)
$ErrorActionPreference = 'Stop'

function FromB64Url([string]$s) {
    $s = $s.Replace('-', '+').Replace('_', '/')
    switch ($s.Length % 4) { 2 { $s += '==' } 3 { $s += '=' } }
    [Convert]::FromBase64String($s)
}

function Payload([string]$token) {
    $parts = $token.Trim().Split('.')
    if ($parts.Length -ne 3 -or $parts[0] -ne 'UCUDEV1') { throw 'not a UCUDEV1 token' }
    [Text.Encoding]::UTF8.GetString((FromB64Url $parts[1])) | ConvertFrom-Json
}

if ($Check) {
    if (-not $PublicKey) { throw '-Check needs -PublicKey' }
    $token = ([IO.File]::ReadAllText($Check)).Trim()
    $parts = $token.Split('.')
    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    $rsa.FromXmlString([IO.File]::ReadAllText($PublicKey))
    $signed = [Text.Encoding]::ASCII.GetBytes($parts[0] + '.' + $parts[1])
    $ok = $parts.Length -eq 3 -and $rsa.VerifyData($signed, 'SHA256', (FromB64Url $parts[2]))
    $p = Payload $token
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $state = if (-not $ok) { 'BAD-SIGNATURE' } elseif ($p.scope -ne 'no-store') { 'WRONG-SCOPE' } elseif ($now -ge $p.exp) { 'EXPIRED' } else { 'VALID' }
    "$state name=$($p.name) dlc=$($p.dlc -join ',') expires=$([DateTimeOffset]::FromUnixTimeSeconds($p.exp).UtcDateTime.ToString('yyyy-MM-dd HH:mm'))Z"
    if ($state -ne 'VALID') { exit 1 }
    exit 0
}

if (-not $Code -or -not $Url) { throw 'needs -Code and -Url (or -Check and -PublicKey)' }
$body = @{ code = $Code } | ConvertTo-Json -Compress
try {
    $r = Invoke-RestMethod -Method Post -Uri ($Url.TrimEnd('/') + '/v1/dev-token') -ContentType 'application/json' -Body $body
} catch {
    $msg = $_.ErrorDetails.Message
    throw "the server refused: $(if ($msg) { $msg } else { $_.Exception.Message })"
}
[IO.File]::WriteAllText($Out, $r.token)
$p = Payload $r.token
"wrote $Out  name=$($p.name) dlc=$($p.dlc -join ',') expires=$([DateTimeOffset]::FromUnixTimeSeconds($p.exp).UtcDateTime.ToString('yyyy-MM-dd HH:mm'))Z"
