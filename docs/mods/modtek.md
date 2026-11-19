# ModTek

Mod source: [ModTek](https://github.com/BattletechModders/ModTek).

Code: [`ModBootstrap.cs`](../../tools/mod-bootstrap/ModBootstrap.cs), [`PR308Guard.cs`](../../tools/mod-bootstrap/PR308Guard.cs),
[`ModAssemblyHook.cs`](../../tools/mod-bootstrap/ModAssemblyHook.cs). Install notes: [`README.md`](../../tools/mod-bootstrap/README.md).

ModTek's files are used as shipped. UCU starts it through its own doorstop target, which fixes what differs between
the 2018 game's Mono and Unity 6's before ModTek loads:

- ModTek's `netstandard.dll` 2.0 would load before the player's own and lacks what Unity 6 needs from 2.1, so the
  player's copy is loaded first.
- Three `Assembly` members ModTek patches moved or changed (a `LoadFrom` overload, `Location`, `LoadFile`). ModTek's
  patches are retargeted to where those members now live.
- Unity 6 ships no `Facades` folder, so mods built against netstandard 1.x contracts fail to load. Those names are
  answered with `netstandard`, which forwards their types.
- `UnityEngine.Input` moved to another module with no forwarder. Modded runs load a copy of `CoreModule` with a
  forwarder added; the player's own file is untouched.
- Newtonsoft.Json is version 13 where the 2018 game had 10. The one internal overload ModTek calls is added back.
- MonoMod.Core before 1.3.5 (ModTek 4.4.x) can place a detour more than 2 GB from its code under Proton, and the patched
  method then jumps to garbage. The guard applies MonoMod's own fix (PR 308) to its page allocator. It does nothing on
  MonoMod.Core 1.3.5 or later, which ModTek 4.5.1 and later carry.

UCU uses UnityDoorstop 4.6.0 rather than the copy ModTek ships, because the older copy crashes every D3D12 start.

**Upstream:** ModTek 4.5.1 or later makes the MonoMod guard unnecessary. The rest are differences in the game's
runtime, not ModTek bugs.
