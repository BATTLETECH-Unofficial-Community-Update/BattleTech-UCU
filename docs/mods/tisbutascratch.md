# TisButAScratch

Mod source: [TisButAScratch](https://github.com/BattletechModders/TisButAScratch).

Code: [`ActorHydrateDeferral.cs`](../../tools/mod-bootstrap/ActorHydrateDeferral.cs),
[`CoopRngSweepDeterminism.cs`](../../tools/mod-bootstrap/CoopRngSweepDeterminism.cs). Kind: combat saves, co-op;
the combat-save part fails on the 2018 game too.

**What broke.** The postfix on `AbstractActor.Hydrate` reads `GetPilot().StatCollection`. `Mech`, `Vehicle` and
`Turret` set the pilot only after `base.Hydrate` returns, so the postfix throws on the first actor and the load fails.

**What UCU does.** On a load, holds the postfix back until the concrete `Hydrate` has returned, then runs it with the
same arguments. The adapter is general and applies to any mod's postfix with that shape.

**Co-op.** TisButAScratch picks each combat injury in its `Pilot.InjurePilot` postfix (the location, the injury and its
effects), and its bleed-out tick, a prefix on `AbstractActor.OnActivationEnd`, draws too. All of it uses the global
`UnityEngine.Random`, which is never in step between co-op peers, so the same hit gave the host a head injury and the
guest a torso bleed. Only with TisButAScratch loaded and only in a co-op combat, UCU seeds `UnityEngine.Random` around those
two methods from a key made of the contract GUID, the scope, the unit's GUID (with the pilot's injuries before the call
and the damage for `InjurePilot`), round and phase, and a finalizer puts the peer's own state back. The two methods are
shared points of the sweep: PanicSystem's and Extended_CE's draws on `OnActivationEnd` use the same scope.

**Upstream:** patch `Mech.Hydrate` (and `Vehicle`, `Turret`) instead of `AbstractActor.Hydrate`, or null-check the pilot.
For co-op, a seed derived from the unit, round and damage would make the rolls agree with no help.
