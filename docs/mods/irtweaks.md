# IRTweaks

Mod source: [IRTweaks](https://github.com/BattletechModders/IRTweaks).

Code: [`IRTweaksSaveCompat.cs`](../../tools/mod-bootstrap/IRTweaksSaveCompat.cs),
[`CoopRngSweepDeterminism.cs`](../../tools/mod-bootstrap/CoopRngSweepDeterminism.cs). Kind: combat saves; co-op. The save part is the same on the 2018 game.

**What broke.** With `ScaleObjectiveBuildingStructure` on, two postfixes, on `TurnDirector.OnInitializeContractComplete`
and on `Building.AttachBuildingToObjective`, scale each objective building's structure once, tracked by a list that
`CombatGameState._Init` clears. The game does both again when a save loads or a co-op guest reloads for a resync, so the
already-scaled structure is scaled a second time.

**What UCU does.** Puts a prefix on `BuildingHelper.ScaleHealth`, which both postfixes call. In a combat from a save it skips
a building whose `StartingStructure` is not its unscaled value (`overrideStructurePoints`, else `BuildingDef.StructurePoints`),
so a building first attached after the load still scales. The save already holds the scaled values. The patch is applied only
after `IRTweaks.Mod.Init` has read its settings, because applying it earlier runs `ModState`'s static constructor too
soon and ModTek then fails IRTweaks and every mod that depends on it.

**Co-op.** IRTweaks draws every combat roll from one time-seeded `IRTweaks.Mod.Random` per process: its pilot-skill bonuses
to a hit's damage, heat, AP and stability, the self-knockdown and forced-shutdown checks after an attack, and the
pain-tolerance check on an injury. The two co-op players rolled separately, so one 'Mech could take a stability bonus on one
PC that the other never saw, and the unit check resynced the guest. In a co-op combat only, each of those three
entry points is a scope (`AttackDirector.OnAttackComplete`, `Pilot.InjurePilot` and CustomAmmoCategories' per-hit
`ApplyVariance`), seeded from the contract GUID and identifiers both players share (the attack sequence, group, weapon and
hit index, the pilot and its injury count, the round and phase), and every `System.Random` draw in IRTweaks' own methods,
`IRTweaks.Mod.Random` included, comes from a generator seeded from that scope. HUD previews stay outside a scope. UCU patches
IRTweaks only once the first combat starts: patching when its assembly loads ran `ModState`'s static constructor before
IRTweaks' `Init`, and ModTek then failed IRTweaks and its dependents.

**Upstream:** skip the scaling of a building restored from a save (in `BuildingHelper.ScaleHealth`, so both callers are covered). For co-op, draw combat rolls from a seed both
players share.
