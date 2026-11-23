# IRTweaks

Mod source: [IRTweaks](https://github.com/BattletechModders/IRTweaks).

Code: [`IRTweaksSaveCompat.cs`](../../tools/mod-bootstrap/IRTweaksSaveCompat.cs),
[`IRTweaksCoopDeterminism.cs`](../../tools/mod-bootstrap/IRTweaksCoopDeterminism.cs). Kind: combat saves; co-op. The save part is the same on the 2018 game.

**What broke.** With `ScaleObjectiveBuildingStructure` on, the postfix on `TurnDirector.OnInitializeContractComplete`
scales each objective building's structure once, tracked by a list that `CombatGameState._Init` clears. The game sends
that message again when a save loads, so the already-scaled structure is scaled a second time.

**What UCU does.** Skips that postfix on a load. The save already holds the scaled values. The patch is applied only
after `IRTweaks.Mod.Init` has read its settings, because applying it earlier runs `ModState`'s static constructor too
soon and ModTek then fails IRTweaks and every mod that depends on it.

**Co-op.** IRTweaks draws every combat roll from one time-seeded `IRTweaks.Mod.Random` per process: its pilot-skill bonuses
to a hit's damage, heat, AP and stability, the self-knockdown and forced-shutdown checks after an attack, and the
pain-tolerance check on an injury. The two co-op players rolled separately, so one 'Mech could take a stability bonus on one
PC that the other never saw, and the unit check resynced the guest. In a co-op combat only, UCU makes each of those three
entry points a seeded scope, with a `System.Random` seeded from the contract GUID and identifiers both players share (the
attack sequence, group, weapon and hit index, the pilot and its injury count, the round and phase), and every read of
`IRTweaks.Mod.Random` in IRTweaks' own methods returns that scope's instance. HUD previews stay outside a scope. UCU patches
IRTweaks only once the first combat starts: patching when its assembly loads ran `ModState`'s static constructor before
IRTweaks' `Init`, and ModTek then failed IRTweaks and its dependents.

**Upstream:** skip the scaling when `CombatGameState.WasFromSave` is true. For co-op, draw combat rolls from a seed both
players share.
