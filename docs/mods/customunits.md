# CustomUnits

Mod source: [CustomUnits](https://github.com/BattletechModders/CustomAmmoCategories/tree/master/CustomUnits).

Code: [`CustomUnitsSaveCompat.cs`](../../tools/mod-bootstrap/CustomUnitsSaveCompat.cs),
[`CustomUnitsCoopContract.cs`](../../tools/mod-bootstrap/CustomUnitsCoopContract.cs),
[`CustomUnitsCoopCompare.cs`](../../tools/mod-bootstrap/CustomUnitsCoopCompare.cs),
[`CustomUnitsCoopFiringArc.cs`](../../tools/mod-bootstrap/CustomUnitsCoopFiringArc.cs),
[`CustomUnitsCoopMoveStat.cs`](../../tools/mod-bootstrap/CustomUnitsCoopMoveStat.cs). Kind: combat saves, co-op.
Depends on a game-code change: saving mod subclasses with the nearest game save contract (see the [index](README.md)).

**What broke.** CustomUnits replaces every mech with its own classes (`CustomMech`, `TrooperSquad`, `QuadMech`,
`FakeVehicleMech`), which have no save contract. A load creates them without running their constructors. Four problems
follow:

1. `TrooperSquad.location_index` and `TrooperSquad.info` are never set.
2. `CustomMech.GetHitTable` reads `MechDef.Chassis` during `BehaviorTree.InitFromSave`, before the game has restored the
   chassis.
3. The postfix on `EncounterLayerParent.InitializeContract` (the player-controlled convoy) reads
   `combat.EncounterLayerData`, which a load sets later.
4. The postfix on `MechDef.RefreshChassis` makes a new melee component ref each time. The load runs it again, so the
   saved melee weapon's ref no longer matches and its def stays null.

**What UCU does.**

1. Sets the two `TrooperSquad` fields to what its field initializer and constructor would have set.
2. Restores the chassis early (the first two steps of `MechDef.InitFromSave`).
3. Skips the convoy postfix on a load. Its result is already in the save.
4. Gives the MechDef back the saved melee ref.

**Co-op.** CustomUnits drops units tagged for other biomes (`NoBiome_` plus the contract's biome) from the lists it
returns for a contract, but only once its static `CurrentContract` is set, and only its postfix on
`SimGameState.StartContract` sets it. A co-op guest launches a contract by another route and never calls `StartContract`,
so its static was null or stale: its unit lists kept units the host had dropped, the seeded draw from them differed, and
the two players spawned different units (a different vehicle at the same spawn point, for one), which the unit check
caught in round one. In a co-op combat only, UCU now sets that static to the starting contract before any unit is
generated, on both players' PCs. Single player is untouched.

CustomUnits also sets a per-player stat, `CUSeenByPlayer`, on a unit when the local player first sees it. Its only reader
is the boss-appear animation. The co-op checks hash every stat on a unit, so when the host's player had seen an enemy
that the guest's had not, the two differed and the check started a resync. UCU's co-op checks now leave that one stat out
(a name list in the game code, empty in UCU; the adapter adds `CUSeenByPlayer` only when CustomUnits is loaded). The stat
and what CustomUnits does with it are unchanged.

CustomUnits also gives every vehicle a firing-arc stat, `CUFiringArc`. It lists each vehicle chassis file a second time
as a mech-shaped chassis (a "fake vehicle") and reads both copies into one record per chassis id. Only the vehicle copy
sets the arc (360 with a turret, 90 without, or the file's own `FiringArc`), and each copy replaces the record, so a
fake vehicle's stat was that value or 0 depending on which copy the game loaded last. The two players could differ,
and the co-op checks (which hash every stat) caught it. In a co-op combat only, UCU now reads the arc CustomUnits
intends from the vehicle file as it is loaded, and sets a fake vehicle's stat to it when its own differs, on both
players' PCs. Single player is untouched.

CustomUnits records each move's cost in a stat, `CULastMoveDistance`, which its move clamp reads. It reads the cost
from the unit's pathing when the move sequence starts, while the other player's copy gets the cost read when the move
order was built. For one AI unit the pathing changed between the two reads, so the stat differed and the co-op checks
caught it. In a co-op combat only, the player who moved the unit now sends the value it holds after the move starts,
and the other player sets that same value before the next check. Single player is untouched.

**Upstream:** for combat saves, see points 1 and 2 of [Combat saves: notes for mod authors](../mod-authors-combat-saves.md). For co-op, set `CurrentContract`
from the contract being generated instead of from one call path, and keep per-player state out of `StatCollection`
(the co-op checks compare every stat). Give a fake vehicle's chassis its own record id, or set `FiringArc` in both
parses.
