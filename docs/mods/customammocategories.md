# CustomAmmoCategories

Mod source: [CustomAmmoCategories](https://github.com/BattletechModders/CustomAmmoCategories/tree/master/CustomAmmoCategories).

Code: [`CustomAmmoCategoriesSaveCompat.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesSaveCompat.cs),
[`CustomAmmoCategoriesCoopBraceModes.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesCoopBraceModes.cs),
[`CustomAmmoCategoriesLoadModes.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesLoadModes.cs),
[`CustomAmmoCategoriesCoopModeSync.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesCoopModeSync.cs),
[`CoopRngSweepDeterminism.cs`](../../tools/mod-bootstrap/CoopRngSweepDeterminism.cs),
[`CustomAmmoCategoriesCoopAmsGeometry.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesCoopAmsGeometry.cs),
[`CustomAmmoCategoriesCoopArtillerySync.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesCoopArtillerySync.cs),
[`LoadSteps.cs`](../../tools/mod-bootstrap/LoadSteps.cs) (the `cac-jamq` row),
[`CacDeferredEffectCoopSync.cs`](../../tools/mod-bootstrap/CacDeferredEffectCoopSync.cs),
[`CacDynamicMapCoopSync.cs`](../../tools/mod-bootstrap/CacDynamicMapCoopSync.cs). Kind: combat saves, co-op;
the combat-save part fails on the 2018 game too.

**What broke.** CustomAmmoCategories skips the game's `ReapplyDesignMasks` call in `AbstractActor.Init` and makes it
later, from `ResetPathing`. In a new battle that's harmless. On a load, that skipped call is the only place
`occupiedEncounterLayerCells` is set, so `CheckEnteredCellsForRegions` reads null and the load stops at the first unit.

**What UCU does.** On a load, before `AbstractActor.Init`, an actor whose list is still null gets the list the skipped
call would have set. CustomAmmoCategories still applies the masks from `ResetPathing`.

**Co-op.** CustomAmmoCategories prefixes the AI behaviour tree's `BraceNode.Tick`. To find a weapon that can fire
indirectly it tries every firing mode of every weapon, keeps the first capable mode of each weapon it selects for its brace
attack, and never puts the others back. A weapon it didn't select stays in the last mode it tried (an enemy's Clan AMS
left Off, for one). The behaviour tree runs only on the PC that thinks for the AI, the co-op host, so the leftover mode
existed there only, the unit check saw `CAC-WeaponMode` differ, and the guest was resynced. In a co-op combat only, UCU
records each weapon's mode and ammo before the node runs, and afterwards puts back, with CustomAmmoCategories' own
`ApplyAmmoMode`, every weapon whose mode or ammo changed and that isn't in the attack order the node returned. Single
player is unchanged.

**Weapon modes on a load.** CustomAmmoCategories registers a weapon's modes only from its `Weapon` constructor postfixes,
and a load builds weapons without that constructor. So CustomActivatableEquipment's `AssignAmmoToWeapons` prefix added a
weapon addon's modes (Artemis IV, the Gyrojet ammo feed) to a throwaway instance, the instance registered later set the
mode to base, an AMS switched Off came back On, and an AI unit loaded from a data mechdef got no addon modes because a
combat save does not carry the ModTek-injected attachment links (`LocalGUID`, `TargetComponentGUID`). On a combat loading
from a save, with combat saves on (the marker or a co-op combat),
[`CustomAmmoCategoriesLoadModes.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesLoadModes.cs) copies each empty link
from the same component ref of the unit's data def, records each weapon's saved mode and ammo, and registers
CustomAmmoCategories' instance before the addon modes are added, then puts back each saved mode and ammo the weapon
lost, with `ApplyAmmoMode`. It writes no stat.

**Co-op: weapon modes between the peers.** A weapon's mode or ammo (`CAC-WeaponMode`, `CurrentAmmoId`) is a stat the
owner sets locally from the HUD, and nothing sent it to the other peer. Patch 166 gives mods a side channel on co-op
traffic: an invocation carries its sender's mod data, which the receiver applies just before the invocation runs; a peer
that has just loaded a resync or rejoin snapshot gets the host's full state; and a change can be sent at once, outside any
invocation, with the host relaying it. [`CustomAmmoCategoriesCoopModeSync.cs`](../../tools/mod-bootstrap/CustomAmmoCategoriesCoopModeSync.cs),
only with CustomAmmoCategories loaded and only in co-op, registers both stats with that channel and sends a HUD change
(`CycleMode`, `CycleAmmo`, `SyncMode`) at once. It never applies incoming data to the receiver's own units, except the
host's full state after a load. Patch 176 applies a click at an order point both peers share: the host holds a client's click and sends it to every client just before the next invocation's echo, keyed to that invocation, and every peer applies it right before that invocation runs. Until then the clicking peer's `MechStatsReporter` reports the weapon's pre-click value, so both peers hash the old value before the order point and the new one from it on. A lost or misapplied click still fails the compare. A snapshot load drops held clicks, and the full state carries the pending values. With three or more players, a third peer's held click is dropped during another peer's load, and the next compare resyncs that peer.

**Co-op: missile placement.** CustomAmmoCategories replaces `AttackSequence.GenerateHitInfo` with its own prefix. For
indirect fire it places each missile with `RandomOnSphere`, which draws the global `UnityEngine.Random.onUnitSphere`, and
that generator is never in step between co-op peers, so the same volley hit different units and locations on each peer.
UCU's own host-authoritative stray shots sit inside the retail code CustomAmmoCategories skips, so they never run with it
loaded. [`CoopRngSweepDeterminism.cs`](../../tools/mod-bootstrap/CoopRngSweepDeterminism.cs), only with
CustomAmmoCategories loaded and only in a co-op combat, seeds `UnityEngine.Random` around the game's private
`AttackSequence.GenerateHitInfo` from the contract GUID, the sequence id, the group index and the weapon index, and around
`GenerateToHitInfo` (CustomAmmoCategories' stray, AMS interception and variance rolls) from the contract GUID and the sequence
id, and puts the peer's own state back. Its jam, unjam, mine, fire and deferred-effect rolls are rows of the sweep's
declaration table. Direct fire's `GetMissPositionDirect` may still raycast each peer's own scene, and CustomAmmoCategories'
random draws at impact time outside `GenerateHitInfo` aren't seeded.

**Co-op: AMS interception geometry.** CustomAmmoCategories samples each missile's trajectory spline every 10 m and fires
every AMS whose owner is in range. The spline runs between this peer's own animated scene poses, so the ends differed by up
to 0.62 m between peers, and one missile's spline had an extra sample in an AMS's range on the host: 16 shots against 15, and
a resync. `CustomAmmoCategoriesCoopAmsGeometry` has the host decide the trajectory points and the other peer use them.

**Co-op: state a resync drops.** CustomAmmoCategories keeps these in static memory, and its `OnCombatGameDestroyed` prefix
empties them on a guest's reload while the host never reloads. Each adapter has the host send its values to a peer that has
just loaded a snapshot, through `ModStateCarry`:
- artillery mode, each artillery weapon's target point and terrain hit positions, with the `irbtmu_immobile_unit` stat
  (`CustomAmmoCategoriesCoopArtillerySync`);
- the jam, AMS-jam and ammo-explosion queues (the `cac-jamq` row of `LoadSteps`). The AMS queue is the only thing that resets
  `CAC-AMShootsCount` at the next round, so a guest without it kept an AMS's shots and could not fire it;
- pending deferred effects such as artillery shells (`CacDeferredEffectCoopSync`);
- minefields, temporary design masks and fires (`CacDynamicMapCoopSync`).

**Upstream:** when skipping `ReapplyDesignMasks`, set `occupiedEncounterLayerCells` if it is null. For the brace check, restore
the modes it tried, or try them on a copy; register a weapon's modes when it is deserialized, not only from its
constructor.
