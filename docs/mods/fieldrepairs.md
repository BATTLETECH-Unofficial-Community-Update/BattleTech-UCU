# FieldRepairs

Mod source: [FieldRepairs](https://github.com/BattletechModders/FieldRepairs).

Code: [`FieldRepairsCoopDeterminism.cs`](../../tools/mod-bootstrap/FieldRepairsCoopDeterminism.cs), with the general
[`CoopSpawnDeterminism.cs`](../../tools/mod-bootstrap/CoopSpawnDeterminism.cs). Kind: co-op.

**What broke.** FieldRepairs rolls random pre-applied damage for each unit (armour, structure, ammo, criticals, pilot
injuries) when the game applies its PoorlyMaintained effect. Every draw comes from one shared, unseeded
`FieldRepairs.Mod.Random`, so in co-op the two players spawn the same unit with different damage, and the battle
desyncs in round 1.

**What UCU does.** In a co-op battle only, around each `PoorlyMaintainedEffect.ApplyEffectsTo*` call and
`CombatGameState.FirstTimeInit`, every read of `Mod.Random` in the mod's own methods is redirected to a `System.Random`
seeded from the contract GUID, the method and the target unit's GUID. `UnityEngine.Random` inside a `RepairState`
constructor in one of those scopes is seeded from the scope's key too. Both players get the same seed regardless of
call order. A draw made outside those calls during a co-op battle is logged, so a new hook that needs seeding shows up.
Single-player is untouched.

**Upstream:** nothing needed for single-player. For co-op, a seed derived from the unit's GUID would make the rolls
agree on both machines without help.
