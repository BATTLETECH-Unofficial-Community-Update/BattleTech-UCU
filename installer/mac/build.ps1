# Publishes the macOS command-line tool: one self-contained single file per CPU, into <Out>\osx-arm64 and <Out>\osx-x64. The SDK
# ad-hoc signs a macOS host it builds, on any OS. Builds are deterministic (UcuMac.csproj): the same commit gives the same bytes.
# Needs the pinned SDK (global.json here) and, for each CPU, two NuGet packages of the SDK's own runtime version (10.0.15 for SDK
# 10.0.404): Microsoft.NETCore.App.Runtime.<rid> and Microsoft.NETCore.App.Host.<rid> (the apphost and singlefilehost), and once
# Microsoft.NET.ILLink.Tasks (single-file publishing; a clean package cache needs it). With -Feed they
# come from a folder of .nupkg files, offline; without it NuGet's configured sources are used. Single-threaded, no build servers.
#   -Out <folder>   where the two folders go (not under tools/release-gate)     -Feed <folder>   a local package folder
param([Parameter(Mandatory)][string]$Out, [string]$Feed)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$Out = [IO.Path]::GetFullPath($Out); if ($Feed) { $Feed = [IO.Path]::GetFullPath($Feed) }
Push-Location $here   # dotnet finds global.json (the pinned SDK) from the current folder, not from the project
try {
foreach ($rid in 'osx-arm64', 'osx-x64') {
    $a = @('publish', "$here\UcuMac.csproj", '-c', 'Release', '-r', $rid, '--self-contained', '-p:PublishSingleFile=true',
        '-p:DisableTransitiveFrameworkReferenceDownloads=true', "-p:BaseIntermediateOutputPath=$Out\obj-$rid\", '-o', "$Out\$rid", '-m:1', '-nodeReuse:false', '-nologo', '-v', 'q')
    if ($Feed) { $a += @('--source', $Feed) }
    & dotnet @a
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    Get-ChildItem "$Out\$rid" | ForEach-Object { "$rid $($_.Name) $($_.Length)" }
}
} finally { Pop-Location }
