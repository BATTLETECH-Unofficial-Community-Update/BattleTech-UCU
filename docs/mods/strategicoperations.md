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
