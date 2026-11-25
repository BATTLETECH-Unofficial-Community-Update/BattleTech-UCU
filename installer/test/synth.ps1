# New-SynthInstalls <dir>: two small installs of random bytes (no game data) that the tools take for a Steam and a GOG
# BATTLETECH: <dir>\steam and <dir>\gog. They differ in level10, where they share no run, and in level10.resS.
function New-SynthInstalls([string]$dir) {
    $files = [ordered]@{ 'BattleTech.exe' = 650000; 'BattleTech_Data\globalgamemanagers.assets' = 200000; 'BattleTech_Data\resources.assets' = 3000000
        'BattleTech_Data\resources.assets.resS' = 2000000; 'BattleTech_Data\sharedassets0.assets' = 1000000
        'BattleTech_Data\level10' = 4000000; 'BattleTech_Data\level10.resS' = 1500000 }
    $seed = 100
    foreach ($n in $files.Keys) {
        $seed++
        foreach ($store in 'steam', 'gog') {
            $b = New-Object byte[] $files[$n]
            (New-Object Random $(if ($store -eq 'gog' -and $n -like '*level10*') { $seed + 100 } else { $seed })).NextBytes($b)
            New-Item -ItemType Directory -Force (Split-Path "$dir\$store\$n") | Out-Null
            [IO.File]::WriteAllBytes("$dir\$store\$n", $b)
        }
    }
    foreach ($n in 'Galaxy64.dll', 'GalaxyCSharpGlue.dll') {
        $seed++; $b = New-Object byte[] 50000; (New-Object Random $seed).NextBytes($b); [IO.File]::WriteAllBytes("$dir\gog\$n", $b)
    }
    [IO.File]::WriteAllText("$dir\gog\goggame-1482783682.info", '{"rootGameId":"1482783682"}')
}
