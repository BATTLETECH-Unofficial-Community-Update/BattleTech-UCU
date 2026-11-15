# Tests ReleasePublish.exe (src/PublishCli.cs) against github-standin.py: check, verify (equal, then one asset changed),
# publish (refused while the data release lacks package.vault, then both releases public), prune (dry run, keep window,
# keep < 3 refused, a draft never touched) and the stand-in guard.
# Touches no live repository and no server but 127.0.0.1. Usage: publish-cli.ps1 -Exe <ReleasePublish.exe> [-Port 18766]
# Prints one PASS/FAIL line per case and "PUBLISH CLI TESTS PASS" or "... FAIL" last.
param([Parameter(Mandatory)][string]$Exe, [int]$Port = 18766)
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('publish-cli-test-' + [guid]::NewGuid().ToString('N'))
$assets = Join-Path $work 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$token = Join-Path $work 'token.txt'; [IO.File]::WriteAllText($token, 'test-token-not-real')
$rng = New-Object Random 7
foreach ($n in 'BattleTechUCU-T-data-01.zip', 'BattleTechUCU-T-data-02.zip', 'BattleTechUCUSetup.exe', 'ucu-manifest.tsv', 'verify.ps1', 'package.vault') {
    $b = New-Object byte[] (1000 + $rng.Next(5000)); $rng.NextBytes($b); [IO.File]::WriteAllBytes((Join-Path $assets $n), $b)
}
$srv = Start-Process -FilePath py -ArgumentList '-3.12', '-I', (Join-Path $PSScriptRoot 'github-standin.py'), '--port', $Port -PassThru -WindowStyle Hidden
$api = "http://127.0.0.1:$Port"
$h = @{ Authorization = 'Bearer test' }
$fails = 0
function Case($name, [bool]$ok, $detail) { if ($ok) { "PASS $name" } else { "FAIL $name :: $detail"; $script:fails++ } }
function Run([string[]]$a) {
    # PowerShell 5.1 turns a native command's stderr into a terminating error under Stop; the exit code is what counts.
    $ErrorActionPreference = 'Continue'
    $out = & $Exe @a 2>&1 | ForEach-Object { "$_" } | Out-String
    return @{ Code = $LASTEXITCODE; Out = $out }
}
try {
    for ($i = 0; $i -lt 50; $i++) { try { Invoke-RestMethod "$api/repos/T/main" -Headers $h | Out-Null; break } catch { Start-Sleep -Milliseconds 200 } }
    # Seed what an upload leaves: drafts in both repositories, data with every asset, main with the installer, manifest, verify.ps1.
    function Seed($repo, $tag, [string[]]$names) {
        $r = Invoke-RestMethod "$api/repos/$repo/releases" -Method Post -Headers $h -ContentType 'application/json' -Body (@{ tag_name = $tag; name = $tag; body = 'b'; draft = $true; prerelease = $false } | ConvertTo-Json)
        $up = $r.upload_url -replace '\{.*\}$', ''
        foreach ($n in $names) { Invoke-RestMethod "$up`?name=$n" -Method Post -Headers $h -ContentType 'application/octet-stream' -InFile (Join-Path $assets $n) | Out-Null }
    }
    $all = 'BattleTechUCU-T-data-01.zip', 'BattleTechUCU-T-data-02.zip', 'BattleTechUCUSetup.exe', 'ucu-manifest.tsv', 'verify.ps1', 'package.vault'
    $main = 'BattleTechUCUSetup.exe', 'ucu-manifest.tsv', 'verify.ps1'
    Seed 'T/data' 'vT' $all; Seed 'T/main' 'vT' $main
    Seed 'T/data' 'vNoVault' ($all | Where-Object { $_ -ne 'package.vault' }); Seed 'T/main' 'vNoVault' $main
    $env:BTUCU_GITHUB_API = "$api/repos/"
    $common = @('--repo', 'T/main', '--data-repo', 'T/data', '--token', $token)

    $r = Run (@('check') + $common + @('--tag', 'vT'))
    Case 'check sees both drafts' ($r.Code -eq 0 -and $r.Out -match 'T/main is reachable; the token can write releases\. Release vT exists \(draft\), 3 files' -and $r.Out -match 'T/data .* Release vT exists \(draft\), 6 files') $r.Out
    $r = Run (@('verify', '--assets', $assets) + $common + @('--tag', 'vT'))
    Case 'verify equal' ($r.Code -eq 0 -and $r.Out -match 'VERIFY OK: 9 assets') $r.Out
    Invoke-RestMethod "$api/_corrupt?repo=T/data&tag=vT&name=BattleTechUCU-T-data-02.zip" -Method Post -Headers $h | Out-Null
    $r = Run (@('verify', '--assets', $assets) + $common + @('--tag', 'vT'))
    Case 'verify catches a changed asset' ($r.Code -eq 1 -and $r.Out -match 'DIFFERS BattleTechUCU-T-data-02\.zip' -and $r.Out -match '1 assets differ') $r.Out
    $r = Run (@('verify', '--assets', $assets) + $common + @('--tag', 'vNoVault'))
    Case 'verify catches a missing asset' ($r.Code -eq 1 -and $r.Out -match 'MISSING package\.vault') $r.Out
    $r = Run (@('publish') + $common + @('--tag', 'vNoVault'))
    Case 'publish refuses a data release without package.vault' ($r.Code -eq 1 -and $r.Out -match 'has no package\.vault') $r.Out
    $r = Run (@('publish') + $common + @('--tag', 'vT'))
    $d = (Invoke-RestMethod "$api/repos/T/data/releases?page=1" -Headers $h) | Where-Object { $_.tag_name -eq 'vT' }
    $m = (Invoke-RestMethod "$api/repos/T/main/releases?page=1" -Headers $h) | Where-Object { $_.tag_name -eq 'vT' }
    Case 'publish makes both public' ($r.Code -eq 0 -and $r.Out -match 'Release vT is public' -and -not $d.draft -and -not $m.draft) $r.Out
    # Prune: 12 published releases in T/prune, each with two data parts, package.vault and the manifest; plus a draft with parts.
    function SeedPublished($repo, $tag, [string[]]$names) {
        $r = Invoke-RestMethod "$api/repos/$repo/releases" -Method Post -Headers $h -ContentType 'application/json' -Body (@{ tag_name = $tag; name = $tag; body = 'b'; draft = $false; prerelease = $false } | ConvertTo-Json)
        $up = $r.upload_url -replace '\{.*\}$', ''
        foreach ($n in $names) { Invoke-RestMethod "$up`?name=$n" -Method Post -Headers $h -ContentType 'application/octet-stream' -InFile (Join-Path $assets $n) | Out-Null }
    }
    foreach ($i in 1..12) { SeedPublished 'T/prune' ("p{0:D2}" -f $i) $all }
    Seed 'T/prune' 'pDraft' $all
    $pc = @('--data-repo', 'T/prune', '--token', $token)
    $r = Run (@('prune') + $pc + @('--dry-run'))
    $rel = Invoke-RestMethod "$api/repos/T/prune/releases?page=1" -Headers $h
    Case 'prune dry run removes nothing' ($r.Code -eq 0 -and $r.Out -match 'Would remove 4 data parts' -and $r.Out -match 'from 2 older releases' -and (($rel | ForEach-Object { $_.assets.Count } | Measure-Object -Sum).Sum -eq 13 * 6)) $r.Out
    $r = Run (@('prune') + $pc + @('--keep', '2'))
    Case 'prune refuses a keep below 3' ($r.Code -eq 1 -and $r.Out -match 'at least 3') $r.Out
    $r = Run (@('prune') + $pc)
    $rel = Invoke-RestMethod "$api/repos/T/prune/releases?page=1" -Headers $h
    $by = @{}; foreach ($x in $rel) { $by[$x.tag_name] = $x }
    $old = 'p01', 'p02' | ForEach-Object { $by[$_] }
    $newer = 'p03', 'p12' | ForEach-Object { $by[$_] }
    Case 'prune strips only the two oldest releases' ($r.Code -eq 0 -and $r.Out -match 'Removed 4 data parts' -and
        -not ($old | Where-Object { ($_.assets | Where-Object { $_.name -match '-data-\d+\.zip$' }) -or $_.assets.Count -ne 4 }) -and
        -not ($newer | Where-Object { $_.assets.Count -ne 6 }) -and $by['pDraft'].assets.Count -eq 6) $r.Out
    Case 'prune keeps vault and manifest and notes the removal' (($old | Where-Object { $_.body -match 'removed by housekeeping' -and ($_.assets.name -contains 'package.vault') -and ($_.assets.name -contains 'ucu-manifest.tsv') }).Count -eq 2 -and $by['p03'].body -eq 'b') ($old | ConvertTo-Json -Depth 4)
    $r = Run (@('prune') + $pc)
    Case 'prune is idempotent' ($r.Code -eq 0 -and $r.Out -match 'Removed 0 data parts') $r.Out
    # publish prunes by itself: a 13th published release pushes p03 out of the window.
    Seed 'T/prune' 'pNew' $all; Seed 'T/prunemain' 'pNew' $main
    $r = Run (@('publish', '--repo', 'T/prunemain', '--data-repo', 'T/prune', '--token', $token, '--tag', 'pNew'))
    $by = @{}; foreach ($x in (Invoke-RestMethod "$api/repos/T/prune/releases?page=1" -Headers $h)) { $by[$x.tag_name] = $x }
    Case 'publish prunes the release that fell out of the window' ($r.Code -eq 0 -and $r.Out -match 'Removed 2 data parts' -and $by['p03'].assets.Count -eq 4 -and $by['p04'].assets.Count -eq 6 -and $by['pNew'].assets.Count -eq 6) $r.Out
    Seed 'T/prune' 'pLast' $all; Seed 'T/prunemain' 'pLast' $main
    $r = Run (@('publish', '--repo', 'T/prunemain', '--data-repo', 'T/prune', '--token', $token, '--tag', 'pLast', '--no-prune'))
    $by = @{}; foreach ($x in (Invoke-RestMethod "$api/repos/T/prune/releases?page=1" -Headers $h)) { $by[$x.tag_name] = $x }
    Case 'publish --no-prune leaves the old parts' ($r.Code -eq 0 -and $r.Out -notmatch 'Removed' -and $by['p04'].assets.Count -eq 6) $r.Out
    $env:BTUCU_GITHUB_API = 'https://example.invalid/repos/'
    $r = Run (@('check') + $common + @('--tag', 'vT'))
    Case 'refuses a non-local API override' ($r.Code -eq 1 -and $r.Out -match 'must be a local stand-in') $r.Out
}
finally {
    Remove-Item Env:\BTUCU_GITHUB_API -ErrorAction SilentlyContinue
    # py.exe starts python.exe as a child: stop every stand-in on this port.
    Get-CimInstance Win32_Process -Filter "Name='python.exe' OR Name='py.exe'" | Where-Object { $_.CommandLine -match "github-standin\.py.*--port $Port" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fails) { "PUBLISH CLI TESTS FAIL ($fails)" } else { 'PUBLISH CLI TESTS PASS' }
