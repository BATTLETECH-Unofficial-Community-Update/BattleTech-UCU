# IRTweaks

Mod source: [IRTweaks](https://github.com/BattletechModders/IRTweaks).

Code: [`IRTweaksSaveCompat.cs`](../../tools/mod-bootstrap/IRTweaksSaveCompat.cs). Kind: combat saves; same on the 2018 game.

**What broke.** With `ScaleObjectiveBuildingStructure` on, the postfix on `TurnDirector.OnInitializeContractComplete`
scales each objective building's structure once, tracked by a list that `CombatGameState._Init` clears. The game sends
that message again when a save loads, so the already-scaled structure is scaled a second time.

**What UCU does.** Skips that postfix on a load. The save already holds the scaled values. The patch is applied only
after `IRTweaks.Mod.Init` has read its settings, because applying it earlier runs `ModState`'s static constructor too
soon and ModTek then fails IRTweaks and every mod that depends on it.

**Upstream:** skip the scaling when `CombatGameState.WasFromSave` is true.
