# FieldRepairs

Mod source: [FieldRepairs](https://github.com/BattletechModders/FieldRepairs).

Code: [`CoopRngSweepDeterminism.cs`](../../tools/mod-bootstrap/CoopRngSweepDeterminism.cs). Kind: co-op.

**What broke.** FieldRepairs rolls random pre-applied damage for each unit (armour, structure, ammo, criticals, pilot
injuries) when the game applies its PoorlyMaintained effect. Every draw comes from one shared, unseeded
`FieldRepairs.Mod.Random`, so in co-op the two players spawn the same unit with different damage, and the battle
desyncs in round 1.

**What UCU does.** In a co-op battle only, each `PoorlyMaintainedEffect.ApplyEffectsTo*` call and
`CombatGameState.FirstTimeInit` is a scope seeded from the contract GUID, the method and the target unit's GUID. Inside it
`UnityEngine.Random` (the `RepairState` constructors' component picks) is seeded from that key, and every `System.Random`
draw in the mod's own methods, `Mod.Random` included, comes from a generator seeded from it. Both players get the same seed
regardless of call order. Single-player is untouched.

**Upstream:** nothing needed for single-player. For co-op, a seed derived from the unit's GUID would make the rolls
agree on both machines without help.
