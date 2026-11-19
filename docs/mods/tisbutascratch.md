# TisButAScratch

Mod source: [TisButAScratch](https://github.com/BattletechModders/TisButAScratch).

Code: [`ActorHydrateDeferral.cs`](../../tools/mod-bootstrap/ActorHydrateDeferral.cs). Kind: combat saves; fails on the 2018 game too.

**What broke.** The postfix on `AbstractActor.Hydrate` reads `GetPilot().StatCollection`. `Mech`, `Vehicle` and
`Turret` set the pilot only after `base.Hydrate` returns, so the postfix throws on the first actor and the load fails.

**What UCU does.** On a load, holds the postfix back until the concrete `Hydrate` has returned, then runs it with the
same arguments. The adapter is general and applies to any mod's postfix with that shape.

**Upstream:** patch `Mech.Hydrate` (and `Vehicle`, `Turret`) instead of `AbstractActor.Hydrate`, or null-check the pilot.
