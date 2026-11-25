# BattleTech Extended: Tactics (BEX:T)

Mod source: BEX:T's Extended_CE, FullXotlTables and MechAffinity (see the [BEX:T wiki](https://battletech-extended-3025.fandom.com/wiki/Getting_Started)).

Code: [`ExtendedCESaveCompat.cs`](../../tools/mod-bootstrap/ExtendedCESaveCompat.cs),
[`ExtendedCEPathingCoopSync.cs`](../../tools/mod-bootstrap/ExtendedCEPathingCoopSync.cs),
[`FullXotlTablesCoopDeterminism.cs`](../../tools/mod-bootstrap/FullXotlTablesCoopDeterminism.cs),
[`MechAffinityLoadDedupe.cs`](../../tools/mod-bootstrap/MechAffinityLoadDedupe.cs),
[`BEXTimelineLoadSkip.cs`](../../tools/mod-bootstrap/BEXTimelineLoadSkip.cs). Kind: combat saves, co-op.

**In-mission saves.** Extended_CE postfixes `SimGameOptionsMenu.CanSave` and `SetSaveTooltip`: during a mission `CanSave` is false
and the tooltip reads "Saving during combat missions disabled", unless its own setting `CombatSavesDontWorkEnableAnyway` is
true. The launcher's in-mission saves box lifted IRTweaks' switch and left BEX:T's save button disabled. With the box ticked,
`ExtendedCESaveCompat` sets that setting true in memory at the first `CombatGameState._Init`, after every mod's `Init`, and
touches none of BEX:T's files.

**Co-op: run movement.** Extended_CE reads the moving unit's `Pathing` when a move completes (the walk heat taken off, the
running to-hit malus, `BracedLastRound`) and in `ToHit.GetSelfSpeedModifier`. Only the peer that planned the move has that
path. The other peer runs the move from the invocation's waypoints, so it kept the full walk heat (26 on the host, 27 on the
guest) and could compute a different to-hit chance. `ExtendedCEPathingCoopSync` sends the planner's path facts (has a path,
cost left, maximum cost, move type) with the movement, melee and attack invocations through the co-op side channel, and the
receiving peer shows those facts to Extended_CE's hooks only while they run, then restores its own.

**Co-op: enemy selection.** FullXotlTables skips the original `UnitSpawnPointOverride.RequestUnit` for a tagged mech or vehicle
with a date and picks from its own era and faction tables with `UnityEngine.Random`. UCU's seeded shuffle never ran for those
units, so each peer drew its own stream and built different opposing lances. `FullXotlTablesCoopDeterminism` seeds the draw from
the contract, lance name and unit index, and clears XotlTable's list of its last three picks at each contract's first pick, so
a peer's earlier picks no longer change its next ones.

**After a load: MechAffinity effects.** MechAffinity applies its passive pilot quirk and affinity effects from a postfix on
`AbstractActor.AddToTeam`. A combat save already holds them, and a load calls `AddToTeam` again, so each load added a second
copy. A co-op host never reloads, so after a resync its guest held the unit's quirk effects twice. `MechAffinityLoadDedupe`
cancels the passive effect a load's `AddToTeam` created when the save already holds one with the same id, target and creator.

**After a load: BEX Timeline conversions.** BEXTimeline's `Mech.InitStats` prefix converts an AI mech's loadout (AC ammo to Tracer
boxes, component swaps) with `UnityEngine.Random` draws at spawn. A combat load calls `InitStats` again, so the converted loadout was
converted a second time, differently on each co-op peer. `BEXTimelineLoadSkip` skips that prefix on a load.

**Upstream:** Extended_CE could read the move's cost from the invocation; FullXotlTables could seed its draws from the lance and
unit index; MechAffinity could skip applying an effect the unit already holds.
