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
[`LoadSteps.cs`](../../tools/mod-bootstrap/LoadSteps.cs) (the `so-coop` row),
[`StrategicOperationsCoopCommandSync.cs`](../../tools/mod-bootstrap/StrategicOperationsCoopCommandSync.cs),
[`StrategicOperationsCoopSwarmDecision.cs`](../../tools/mod-bootstrap/StrategicOperationsCoopSwarmDecision.cs),
[`StrategicOperationsCoopAiCommand.cs`](../../tools/mod-bootstrap/StrategicOperationsCoopAiCommand.cs),
[`StrategicOperationsCoopDropPod.cs`](../../tools/mod-bootstrap/StrategicOperationsCoopDropPod.cs). Kind: co-op.

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
`OnCombatGameDestroyed` empties it on a guest's reload while the host keeps it. The `so-coop` row of `LoadSteps` has
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

**AI command abilities.** When an enemy unit uses a command ability (a battle-armour or vehicle drop, a strafe), StrategicOperations
runs it inside `AITeam.makeInvocationFromOrders`, which only the host's AI runs: it stores the drawn unit and waves, calls
`Ability.Activate(unit, posA, posB)` itself and sends only a reserve order. The spawn, its flares, the cooldown and the creator effects
happened on the host alone, so the guest had no `SUPPORT` units, reported them missing at the next compare and hit the resync limit.
`StrategicOperationsCoopAiCommand` records that activation (ability, positions, the unit resource, waves and pilot the host drew,
`UnityEngine.Random.state`) in `CoopDecisionChannel`; it rides the reserve order, and each guest runs the same `Activate` right
before it with the host's drawn values, so it draws nothing itself and the spawned unit GUIDs come from the same counters in the same
order. A guest never thinks for an AI unit; a local `Activate` on one is blocked and logged as an error. Co-op only.

**Drop-pod landings.** A unit a command ability drops is created at `y = -1000` and lands from a coroutine on each player's own
clock: about 4.5 s later `DropPodSpawner.TeleportUnitToSpawnPoint` teleports the *shared* `Unit` to the shared `DropPodPosition`,
and 2 s after that `DeployEvasion` gives it its deploy pips. The two players' clocks disagree, so a compare in between
found the unit landed on one peer and in the pod on the other. `StrategicOperationsCoopDropPod` makes the host's landing the one
that counts for an AI drop: the host records the unit that `TeleportActor` actually landed, its position and rotation, the pose the
unit has when the invocation carrying the record is sent (an AI unit can already have moved by then), and the evasion pips; each
guest skips its own landing and pips and applies the host's right before the invocation they rode, ending on the carried pose. From its
creation until that record is sent (host) or applied (guest) the unit is left out of the MECHS compare data of that peer, so the
compare never sees it half-dropped. The release is the record, never a timer. The host's hold set rides a co-op snapshot, so a guest that
loads one mid-drop holds the same units from its load; a land record applies only to a unit still in the pod. Player-ability drops are unchanged.

**Upstream:** the drop pod is one `DropPodSpawner` component whose `Unit`, `DropPodPosition` and `DropPodRotation` each drop
overwrites. A second drop less than about 4.5 s after the first replaces them before the first coroutine reaches
`TeleportUnitToSpawnPoint`, so that coroutine lands the second unit, the first unit stays at `y = -1000` until it moves, and
`DeployEvasion` runs twice on the second unit. This happens in single player too and UCU leaves it as it is. Capturing the unit,
position and rotation in the coroutine (one `DropPodSpawner` or one closure per drop) would fix it.

**Upstream:** a seed derived from the contract and the unit's GUID would make the rolls agree with no help.
