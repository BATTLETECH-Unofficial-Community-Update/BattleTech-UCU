# MissionControl

Mod source: [MissionControl](https://github.com/CWolfs/MissionControl).

Code: [`MissionControlSaveCompat.cs`](../../tools/mod-bootstrap/MissionControlSaveCompat.cs),
[`MissionControlCoopDeterminism.cs`](../../tools/mod-bootstrap/MissionControlCoopDeterminism.cs). Kind: combat saves, co-op.
The encounter record (part 2 below) isn't published yet: part of it still repeats game code, and it will follow once
that part is rewritten.

**Combat saves, part 1: the pathfinder.** MissionControl builds a hidden pathfinder mech (GUID ending `.9999999999` or
`.9999999998`). It isn't a combatant, but other mods' effects attach to it, and the save writes those effects without
the mech. On load they find no target and the load fails. UCU leaves effects on that pathfinder out of the save; the
battle in progress keeps them.

**Combat saves, part 2: the encounter.** MissionControl builds lances, spawn points, regions, objectives, dialogue and
trigger results while a battle is set up, and switches itself off on a load. The game restores encounter objects only
onto objects already in the map scene, so a load lost everything MissionControl built. UCU snapshots the scene before
MissionControl runs, records what was added or changed at each save (appended after the game's own block, which the
game never reads past), and rebuilds it on load before the save is applied. MissionControl's trigger results are saved
as JSON of their fields and loaded back as their own types.

**Co-op.** MissionControl rolls its encounter rule, extra lances and spawn placement from each player's own random
stream and `Guid.NewGuid`, so co-op players ran different battles. In co-op only, UCU seeds `UnityEngine.Random` from
the contract GUID around those two setup points, and gives MissionControl's own methods deterministic GUIDs.
`Guid.NewGuid` itself is not patched.

**Upstream:** on a save load, rebuild what MissionControl added (or save it yourself); in co-op, draw from a seed both
players share.
