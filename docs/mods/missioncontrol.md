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
as JSON of their fields and loaded back as their own types. For a contract type MissionControl adds (DuoDuel and
DefenceInDepth, for example), the rebuilt layer made MissionControl treat the contract as a stock one on load and skip
its building data, so the load failed; after such a load UCU puts back what MissionControl sets on a fresh start. Stock
contract types take the old path.

When MissionControl adds an extra lance, the load replays it, and then MissionControl's own `InitializeContract` prefix
built it a second time (`AddDestroyWholeUnitChunk`, `AddObjectiveToAutocompleteTrigger` and
`AddExtraLanceSpawnPoints`): a second copy with the same GUIDs, and no message memory for either, so the log filled with
"Duplicate guid detected" and "No MessageMemory for OwnerID". On a load where the replay made objects, UCU now skips those
three builders for that one `InitializeContract`. Fresh starts and loads with nothing replayed are unchanged. This only
showed when MissionControl had rolled an extra lance.

The save record's load patch (on `SerialBox<T>.LoadSerialBox`) first ran the game's shared load body for game cargo, and
that body nulled every cargo type but the first it was compiled for, so a lance spawner's AI orders loaded as null, the
lance never spawned, and in co-op the two players' unit counts differed. The patch now loads every cargo itself and checks
it against the box's own type. This applied in 2.2.2-3B and 2.2.3-3B, with in-mission saves on.

**Encounter types in a save.** Five of MissionControl's encounter types (`SwapPlacementGameLogic`, `SwapTeamFactionGameLogic`,
`DisablePilotDeathGameLogic`, `DialogueActivator`, `SetChunkStateAtRandomActivator`) subclass `EncounterObjectGameLogic`,
which is abstract and a saved class. The game gave that class only a skeleton contract, so the save dropped
`EncounterLayerData.AllEncounterObjectGameLogics` and the load found no encounter objects. UCU's game code now generates
the abstract class's contract once, and its mod subclasses save with it (with in-mission saves on, or in a co-op combat).

**Co-op.** MissionControl rolls its encounter rule, extra lances and spawn placement from each player's own random
stream and `Guid.NewGuid`, so co-op players ran different battles. In co-op only, [`CoopRngSweepDeterminism.cs`](../../tools/mod-bootstrap/CoopRngSweepDeterminism.cs) seeds
`UnityEngine.Random` from the contract GUID around `Contract.BeginRequestResources` and `EncounterLayerParent.InitializeContract`,
around each spawn point and each result node that places units (rows of its declaration table), and gives MissionControl's
own `Guid.NewGuid` call sites deterministic GUIDs. `Guid.NewGuid` itself is not patched.

`MissionControlCoopDeterminism.cs` now only syncs the drop data. MissionControl learns the player's drop (difficulty, skull
rating, tonnage) only from the lance screen, which only the host clicks, so a guest's values stayed 0 and its additional-lance
logic ran on other data. In a co-op combat both players compute them from the contract's lance.

**Upstream:** on a save load, rebuild what MissionControl added (or save it yourself); in co-op, draw from a seed both
players share.
