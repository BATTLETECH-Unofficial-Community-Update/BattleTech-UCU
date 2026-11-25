# PanicSystem

Mod source: [PanicSystem](https://github.com/BattletechModders/PanicSystem).

Code: [`CoopRngSweepDeterminism.cs`](../../tools/mod-bootstrap/CoopRngSweepDeterminism.cs),
[`LoadSteps.cs`](../../tools/mod-bootstrap/LoadSteps.cs) (the `panic-coop` row). Kind: co-op.

**What broke.** PanicSystem rolls a pilot's panic saving throw with Unity's global random generator when an attack
completes, a move or melee ends, or an activation ends. The two co-op peers' generators are never in step, so the same
damage could give one peer a successful save and the other an Unsettled pilot. The guest then diverged, and the compare
resynced it.

**What UCU does.** Only with PanicSystem loaded and only in a co-op combat, `UnityEngine.Random` is seeded around
`AttackDirector.OnAttackComplete`, `ActorMovementSequence.CompleteOrders` and `MechMeleeSequence.CompleteOrders` from a key
made of the contract and the shared sequence, round and phase, and the generator's state is put back afterwards.
`AbstractActor.OnActivationEnd` is the same scope TisButAScratch's draws use (one scope per method). It is installed at
the first combat.

**Co-op: the damage tracker across a resync.** PanicSystem keeps its pending victims, damage baselines and panic levels only
in memory. The guest's resync reload resets them and the host keeps its own, so the host rolled panic saves the guest never
rolled and a unit was Unsettled on the host only. The `panic-coop` row of [`LoadSteps.cs`](../../tools/mod-bootstrap/LoadSteps.cs),
in a co-op combat with PanicSystem loaded, has the host send that state to the guest over the co-op side channel (patch 166, key
`panic-coop`) when the guest reports its snapshot loaded. It logs `panic-coop|capture` and `apply` rows.

**Upstream:** a seed derived from the contract, round and attack would make the rolls agree with no help.
