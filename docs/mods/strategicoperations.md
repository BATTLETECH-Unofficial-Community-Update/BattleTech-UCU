# StrategicOperations

Mod source: [StrategicOperations](https://github.com/BattletechModders/StrategicOperations).

Code: [`ActorHydrateDeferral.cs`](../../tools/mod-bootstrap/ActorHydrateDeferral.cs),
[`EncounterLoadDeferral.cs`](../../tools/mod-bootstrap/EncounterLoadDeferral.cs). Kind: combat saves; fails on the 2018 game too.

**What broke.** Two postfixes run before the state they read exists on a load:

- The postfix on `AbstractActor.Hydrate` reads `GetStaticUnitTags()` (the MechDef), which `Mech`, `Vehicle` and `Turret`
  set only after `base.Hydrate` returns.
- The postfix on `EncounterLayerData.BuildItemRegistry` creates an AI support team. `Team.InitStats` reads
  `combat.EncounterLayerData`, which a load sets later, so the loaded battle never leaves its load screen.

**What UCU does.** Holds those postfixes back on a load and runs them, in their original order with the same arguments,
once the actor is whole or the encounter layer is set. New battles aren't affected. Both adapters are general: they
apply to any mod's postfix with the same shape.

**Upstream:** see point 2 of [Combat saves: notes for mod authors](../mod-authors-combat-saves.md).

## Co-op: shared random rolls

Code: [`StrategicOperationsCoopDeterminism.cs`](../../tools/mod-bootstrap/StrategicOperationsCoopDeterminism.cs),
[`StrategicOperationsCoopStateSync.cs`](../../tools/mod-bootstrap/StrategicOperationsCoopStateSync.cs),
[`StrategicOperationsCoopCommandSync.cs`](../../tools/mod-bootstrap/StrategicOperationsCoopCommandSync.cs),
[`StrategicOperationsCoopSwarmDecision.cs`](../../tools/mod-bootstrap/StrategicOperationsCoopSwarmDecision.cs). Kind: co-op.

**What broke.** StrategicOperations draws its rolls (support-ability picks, deswarm, swarm and strafe targets,
battle-armour death and mount checks, damage-location prefixes) from one time-seeded `StrategicOperations.Mod.Random`, and
its AI ability generation also draws from `UnityEngine.Random`. In co-op the two players rolled them separately, so a
unit one player had was missing on the other's side. The mod also builds its AI teams and strafe, flare and command-lance
units with `Guid.NewGuid`, so each player ended with other team GUIDs.

**What UCU does.** In a co-op contract only, each read of `Mod.Random` in the mod's own methods (20 reads in 12
methods) returns an instance seeded from the contract GUID, the read site, the actor or team involved and how many
earlier reads of that site had that key. `GenerateAIStrategicAbilities` seeds `UnityEngine.Random` around the call and
puts its state back. The team and unit GUIDs the mod mints are derived from the contract GUID, the method and a
per-method counter. Outside a co-op contract the mod's own instance is returned, so single player is untouched.

**State across a resync.** StrategicOperations keeps which battle armor rides, swarms or garrisons which unit, its airlifts,
resupply, command units and uses, strafes in flight and unit coordinates in static `ModState` only; `ModState.ResetAll` at
`OnCombatGameDestroyed` empties it on a guest's reload while the host keeps it. `StrategicOperationsCoopStateSync` has
`ModStateCarry` send the host's values to a peer that has just loaded a snapshot. `ModState.CommandAbilities` (the strafe and
spawn-turret abilities) is built only at `TurnDirector.StartFirstRound`, which a snapshot load does not run, so the guest's
list stayed empty and SO's `Team.ActivateAbility` prefix skipped every command ability there. `StrategicOperationsCoopCommandSync`
has the host send each ability's def id, cooldown, uses left and activations, and the guest makes the abilities as SO does.
A strafe or spawn in flight at a resync can't be given to a guest: the host drops it, logs a `so-cmd-coop|gap-dropped` row and
refunds its uses, cooldown, command uses, contract uses and the company stat. Support teams are relinked on a load.

**Swarm decisions.** SO decides for a battle-armor unit on one peer only: the host's AI (a swarming unit with no weapon
dismounts, a swarmed unit deswarms, a swarming unit's melee picks its location) and the clicking peer's Done button.
`StrategicOperationsCoopSwarmDecision` records each decision on its authority (the host for an AI unit, the owner for a
player unit) in `CoopDecisionChannel`, which sends it with the next invocation; the other peers replay the same call with the
recorded random state and outcome forced.

**Upstream:** a seed derived from the contract and the unit's GUID would make the rolls agree with no help.
