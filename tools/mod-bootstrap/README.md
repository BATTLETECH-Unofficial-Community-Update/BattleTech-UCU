# UCU mod bootstrap

Lets stock ModTek v4 run on UCU's Unity 6 Mono player. `ModBootstrap.cs` says what it fixes and why.

## Install beside the player

| File | From |
|---|---|
| `winhttp.dll` | UnityDoorstop v4.6.0, `x64/winhttp.dll` in `doorstop_win_release_4.6.0.zip`. **Not** the copy in `Mods/ModTek/DoorStop/` |
| `doorstop_config.ini` | this folder |
| `BattleTechPortBootstrap.dll` | built from this folder |
| `Mods/ModTek/` | stock ModTek, unchanged |

Download: <https://github.com/NeighTools/UnityDoorstop/releases/tag/v4.6.0>

| Pin | SHA-256 |
|---|---|
| `doorstop_win_release_4.6.0.zip` | `d423e73e4a92cb168565ac53031f04082daa17133057e53c24343d580c260b25` |
| `x64/winhttp.dll` | `0f39399eba05dea9419781187ae2a6b0555afcdf1859f1319b6213be7729013b` |

Doorstop is LGPL 2.1: ship its `LICENSE` with it. The copy here is `DOORSTOP-LICENSE.txt`.

## In a release

`stage-modsupport.ps1` builds the bootstrap and fills `<player>\ModSupport` with the four files above plus
`ModSupport-README.txt` as `README.txt`. Doorstop only reads files beside the exe; the installer's *Mod support* box
(default on, `tools/release-gate/README.md` → Run the installer) copies them there. With no `Mods\ModTek\lib\ModTek.Preloader.dll`
the bootstrap returns at once and writes a one-line `BattleTechPortBootstrap.log`, so vanilla installs run with it on.
`tools/bta-link` copies a BTA install's mods and saves.

When ModTek is present, the bootstrap also patches both asset-bundle path methods after ModTek's prefixes. For the exact `shaders` bundle, it redirects only a result that still names retail's bundle and only when `BattleTech_Data\StreamingAssets\ucu\bundles\shaders` exists; a mod-provided path is left alone. The `AssetBundleNameToFileURL` result keeps its `file://` form. Without ModTek, the bootstrap returns before registering this adapter, so the vanilla path is unchanged.

## Why not ModTek's doorstop

ModTek 4.4.10's `winhttp.dll` crashes every D3D12 start. Unity delay-loads `d3d12.dll` by ordinal, and that doorstop's `GetProcAddress` hook
compares the ordinal as a string. It works on D3D11 only. v4.6.0 checks first, and full BTA boots to its main menu on both D3D11 and D3D12
with it. UCU supports both APIs, modded and unmodded.
