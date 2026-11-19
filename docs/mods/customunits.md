# CustomUnits

Mod source: [CustomUnits](https://github.com/BattletechModders/CustomAmmoCategories/tree/master/CustomUnits).

Code: [`CustomUnitsSaveCompat.cs`](../../tools/mod-bootstrap/CustomUnitsSaveCompat.cs). Kind: combat saves.
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

**Upstream:** see points 1 and 2 of [Combat saves: notes for mod authors](../mod-authors-combat-saves.md).
