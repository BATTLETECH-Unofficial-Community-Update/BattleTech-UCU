# CustomActivatableEquipment

Mod source: [CustomActivatableEquipment](https://github.com/BattletechModders/CustomActivatableEquipment).

Code: [`CustomActivatableEquipmentAuraLoadCompat.cs`](../../tools/mod-bootstrap/CustomActivatableEquipmentAuraLoadCompat.cs),
[`CustomActivatableEquipmentCoopToggle.cs`](../../tools/mod-bootstrap/CustomActivatableEquipmentCoopToggle.cs),
[`CustomActivatableEquipmentRepairCoopSync.cs`](../../tools/mod-bootstrap/CustomActivatableEquipmentRepairCoopSync.cs).
Kind: combat saves, co-op.

**What broke.** CustomActivatableEquipment remembers which aura effects a unit holds in
`AuraActorBody.affectedAurasEffects`, a per-combat field that starts empty for each unit and isn't in a save. A load
restores the aura effects themselves (`EffectManager.Hydrate`), so the first trigger enter after it finds the aura
missing from the body and `ApplyAuraEffects` creates every effect again. Aura data uses `stackLimit` -1, which retail's
`CreateEffect` doesn't guard, so both copies apply. A Guardian ECM's `GECMInterference` was on its carrier twice.

**What UCU does.** Only with CustomActivatableEquipment loaded, on a combat loading from a save with combat saves on (the
marker or a co-op combat), a prefix on `AuraActorBody.ApplyAuraEffects` puts each effect the call would create that a
live effect on the body's unit already carries into the body's own list, as `ApplyAuraEffects` does itself, so its own
check skips the second copy. It logs `cae-aura-load|patched` and an `adopted` row per aura.

The adapter's gate is `CombatGameState.WasFromSave`, which `Hydrate` sets and which stays true for the whole loaded
combat. It first used `IsLoadingFromSave`, which `TurnDirector.OnEncounterBegin` clears. Guardian ECM self auras apply
before Encounter Begin, but the C3 slave ally aura (`Gear_C3__Slave_SlaveOverwhelm`, `stackLimit` -1) re-applies a few
seconds after it, so the adapter never adopted the restored copy and a co-op guest held `ADD_ENCOUNTER_TAG_C3_Network`
-2 against the host's -1 after every resync.

**Co-op: HUD toggles.** A player turns a component on or off in the combat HUD, which calls
`ActivatableComponent.toggleComponentActivation` on the clicking peer only: the activation fail roll, a failed activation's
crits and damage, and the component's active-state stats and effects. No invocation carried any of it. In a co-op combat,
`CustomActivatableEquipmentCoopToggle` records a toggle made outside an invocation on its authority (the owner for a player
unit, the host for an AI unit) with the roll's result and `UnityEngine.Random.state`, sends it at once through
`CoopDecisionChannel`, and the other peers make the same toggle at the click's order point with the outcome forced.

**Co-op: repair state across a resync.** `RepairHelper.DamagedStructureLocationsThisTurn` gathers the locations a unit was
damaged in since its last commit, and `CommitCAEDamageData` reads it at `OnNewRound` to fire repair components. A guest's
reload empties it and replaces the actor objects it is keyed by, so only the host repaired at the next round.
`CustomActivatableEquipmentRepairCoopSync` sends the host's sets to a peer that has just loaded a snapshot. It carries them
instead of rebuilding them from the saved `<location>_damageRound` stats, which hold a different location and a different round.

**Upstream:** rebuild `affectedAurasEffects` from the unit's live effects when a body is created on a load.
